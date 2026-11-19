namespace Autheris.Application.Products;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>The stage-1 result a read is served from: <see cref="Stale"/> when it is older than the load mode allows.</summary>
public sealed record ProductResult(TableIdentifier CacheKey, DateTimeOffset LoadedAt, bool Stale);

/// <summary>
/// Stage 1 of a data product view: loads the current definition under limits and a cluster lease into the table cache (shadow swap),
/// per load mode. A failed load keeps the previous result and marks the product stale. Stage 2 (the caller's query on the result)
/// is decided by the decision point on the product object.
/// </summary>
public sealed class ProductLoadService(
    IDataProductRepository products,
    IProductQueryRunner runner,
    ITableCacheStore store,
    ITableCacheCatalog catalog,
    IDistributedClusterStateProvider cluster,
    IAuditWriter audit,
    TimeProvider? timeProvider = null,
    ILogger<ProductLoadService>? logger = null,
    IProductCatalogStore? catalogStore = null,
    IProductBaseObjectCheck? baseObjects = null,
    ProductUsageTracker? usage = null) : IProductRefreshTimes
{
    private static readonly TimeSpan LeaseSlack = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<TableIdentifier, Lazy<Task<ProductResult>>> _inFlight = new();
    private readonly ConcurrentDictionary<TableIdentifier, DateTimeOffset> _lastAccess = new();
    private readonly ConcurrentDictionary<TableIdentifier, (TenantId Tenant, string Schema, string Name)> _tracked = new();
    private readonly ConcurrentDictionary<TableIdentifier, DateTimeOffset> _lastAttempt = new();
    private readonly ConcurrentDictionary<TableIdentifier, bool> _deactivated = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>True when a load of the product was started within <paramref name="span"/> (successful or not): the schedule worker never retries faster than the interval.</summary>
    public bool AttemptedWithin(DataProduct product, TimeSpan span) =>
        _lastAttempt.TryGetValue(ProductSource.CacheKey(product.Tenant, product.Schema, product.Name), out var at) && _time.GetUtcNow() - at < span;

    /// <summary>The load time of the current stage-1 result, or null when the product has none.</summary>
    public DateTimeOffset? LoadedAt(DataProduct product) =>
        catalog.TryGet(ProductSource.CacheKey(product.Tenant, product.Schema, product.Name), out var meta) && meta is not null ? meta.LastRefreshedAt : null;

    /// <summary>The limits of a load: the product limit lowered to the global cap (a zero means no limit on that side).</summary>
    internal static ProductLimits EffectiveLimits(ProductLimits product, ProductLoadCaps caps) => new(
        Cap(product.MaxRows, caps.MaxRows),
        Cap(product.MaxBytes, caps.MaxBytes),
        (int)Cap(product.MaxRuntimeSeconds, caps.MaxRuntimeSeconds));

    /// <summary>The global caps of a load: fixed limits of the gateway, not configuration (tests set them per instance).</summary>
    internal ProductLoadCaps Caps { get; init; } = ProductLoadCaps.Default;

    private static long Cap(long requested, long cap) => cap <= 0 ? requested : requested <= 0 ? cap : Math.Min(requested, cap);

    /// <summary>
    /// Loads the product now (schedule trigger, manual load, on-demand miss, live query). Runs only the current definition: a caller
    /// that holds an older revision, or a stored definition that does not match its hash, is refused.
    /// </summary>
    public async Task<ProductResult> LoadAsync(DataProduct requested, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var current = await products.GetProductAsync(requested.Tenant, requested.Schema, requested.Name, ct).ConfigureAwait(false);
        if (current is null || current.Status == DataProductStatus.Retired || current.Kind != DataProductKind.View)
        {
            throw new ProductLoadException(ProductErrorCodes.Unavailable);
        }

        if (!string.Equals(current.DefinitionHash, requested.DefinitionHash, StringComparison.Ordinal) ||
            !string.Equals(ProductDefinitions.Hash(current.Definition), current.DefinitionHash, StringComparison.Ordinal))
        {
            throw new ProductLoadException(ProductErrorCodes.DefinitionChanged);
        }

        var key = ProductSource.CacheKey(current.Tenant, current.Schema, current.Name);
        var limits = EffectiveLimits(current.Limits, Caps);
        var runtime = limits.MaxRuntimeSeconds > 0 ? TimeSpan.FromSeconds(limits.MaxRuntimeSeconds) : TimeSpan.FromHours(1);
        var held = await cluster.TryAcquireLeaseAsync($"product-load:{key}", runtime + LeaseSlack, ct).ConfigureAwait(false)
            ?? throw new ProductLoadException(ProductErrorCodes.LoadInProgress);
        await using var lease = held;

        var started = _time.GetUtcNow();
        _lastAttempt[key] = started;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(runtime);
            // every run re-checks that the base objects are still active; a retired or deactivated one makes the product invalid
            if (baseObjects is not null && !await baseObjects.AllActiveAsync(current.BaseObjects, timeout.Token).ConfigureAwait(false))
            {
                throw new ProductLoadException(ProductErrorCodes.BaseObjectInactive);
            }

            var result = await StageAsync(current, key, limits, started, timeout.Token).ConfigureAwait(false);
            await SetStatusAsync(current, DataProductStatus.Active, ct).ConfigureAwait(false);
            var rowsLoaded = catalog.TryGet(key, out var m) ? m?.RowCount : null;
            await audit.RecordRequiredAsync(Entry(AuditEventTypes.ProductLoaded, current, new { rows = rowsLoaded }, started), ct).ConfigureAwait(false);
            usage?.RecordLoad(current.Tenant, current.Id, true, rowsLoaded ?? 0, (long)(_time.GetUtcNow() - started).TotalMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var code = ex switch
            {
                ProductLoadException known => known.Code,
                OperationCanceledException => ProductErrorCodes.LimitExceeded, // the runtime limit
                _ => ProductErrorCodes.LoadFailed
            };
            logger?.LogWarning("Data product load failed with {Code} ({ExceptionType}).", code, ex.GetType().Name);
            // the previous result stays; a product without one is invalid until a load succeeds
            await SetStatusAsync(
                current, code != ProductErrorCodes.BaseObjectInactive && catalog.TryGet(key, out _) ? DataProductStatus.Stale : DataProductStatus.Invalid, ct).ConfigureAwait(false);
            usage?.RecordLoad(current.Tenant, current.Id, false, 0, 0);
            await audit.RecordBestEffortAsync(Entry(AuditEventTypes.ProductLoadFailed, current, new { code }, started), ct).ConfigureAwait(false);
            throw ex as ProductLoadException ?? new ProductLoadException(code);
        }
    }

    private async Task<ProductResult> StageAsync(DataProduct product, TableIdentifier key, ProductLimits limits, DateTimeOffset started, CancellationToken ct)
    {
        var query = await runner.RunAsync(new ProductDefiner(product, limits), ct).ConfigureAwait(false);
        var counter = new Counter();
        var epoch = started.ToUnixTimeMilliseconds();
        string shadow;
        try
        {
            shadow = await store.StageShadowGenerationAsync(key, epoch, query.Columns, Limited(query.Rows, limits, counter, ct), ct).ConfigureAwait(false);
        }
        catch
        {
            await store.DropGenerationAsync(key, epoch, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var expires = product.Load.Mode switch
        {
            LoadMode.OnDemand => started.AddMinutes(product.Load.TtlMinutes),
            LoadMode.Live => started,
            _ => (DateTimeOffset?)null
        };
        var meta = await store.SwapActiveGenerationAsync(key, epoch, shadow, counter.Rows, counter.Bytes, expires, ct).ConfigureAwait(false);
        catalog.Upsert(meta with { CachedColumns = query.Columns, IsHealthy = true, LastError = null, LastRefreshedAt = started, ExpiresAt = expires });
        return new ProductResult(key, started, false);
    }

    private sealed class Counter
    {
        public long Rows;
        public long Bytes;
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Limited(
        IAsyncEnumerable<IReadOnlyList<object?>> rows, ProductLimits limits, Counter counter, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
        {
            counter.Rows++;
            counter.Bytes += row.Sum(static v => v switch { null => 1L, string s => s.Length * 2L, byte[] b => b.Length, _ => 8L });
            if ((limits.MaxRows > 0 && counter.Rows > limits.MaxRows) || (limits.MaxBytes > 0 && counter.Bytes > limits.MaxBytes))
            {
                throw new ProductLoadException(ProductErrorCodes.LimitExceeded);
            }

            yield return row;
        }
    }

    /// <summary>
    /// The result a read of <paramref name="product"/> is served from. <c>live</c> loads per query; <c>onDemand</c> loads on the first
    /// access and after ttl expiry, one load at a time (concurrent callers share it); <c>schedule</c> serves the last scheduled load.
    /// A result older than <c>MaxStaleness</c> is never served (<c>PRODUCT_STALE</c>).
    /// </summary>
    public async Task<ProductResult> GetResultAsync(DataProduct product, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var key = ProductSource.CacheKey(product.Tenant, product.Schema, product.Name);
        _lastAccess[key] = _time.GetUtcNow();
        if (product.Load.Mode == LoadMode.OnDemand)
        {
            _tracked[key] = (product.Tenant, product.Schema, product.Name);
        }

        if (product.Load.Mode == LoadMode.Live)
        {
            return await LoadAsync(product, ct).ConfigureAwait(false);
        }

        var state = StateOf(product, key, out var loadedAt);
        if (state == ProductFreshnessState.Fresh)
        {
            await EnsureBaseObjectsActiveAsync(product, ct).ConfigureAwait(false);
            return new ProductResult(key, loadedAt!.Value, false);
        }

        if (state == ProductFreshnessState.TooStale)
        {
            throw new ProductLoadException(ProductErrorCodes.Stale);
        }

        if (product.Load.Mode == LoadMode.Schedule)
        {
            return state == ProductFreshnessState.NotLoaded
                ? throw new ProductLoadException(ProductErrorCodes.Unavailable)
                : new ProductResult(key, loadedAt!.Value, true);
        }

        return await SingleFlightAsync(product, key, state, loadedAt, ct).ConfigureAwait(false);
    }

    /// <summary>A cached result is never served once a base object was deactivated: the product turns invalid and the read fails like any unavailable product.</summary>
    private async Task EnsureBaseObjectsActiveAsync(DataProduct product, CancellationToken ct)
    {
        if (baseObjects is null || await baseObjects.AllActiveAsync(product.BaseObjects, ct).ConfigureAwait(false))
        {
            return;
        }

        var current = await products.GetProductAsync(product.Tenant, product.Schema, product.Name, ct).ConfigureAwait(false);
        if (current is not null && current.Status != DataProductStatus.Retired)
        {
            await SetStatusAsync(current, DataProductStatus.Invalid, ct).ConfigureAwait(false);
        }

        throw new ProductLoadException(ProductErrorCodes.BaseObjectInactive);
    }

    private async Task<ProductResult> SingleFlightAsync(DataProduct product, TableIdentifier key, ProductFreshnessState before, DateTimeOffset? loadedAt, CancellationToken ct)
    {
        // the load itself must not die with the first waiter that cancels: it runs on its own token, waiters only stop waiting
        var flight = _inFlight.GetOrAdd(key, _ => new Lazy<Task<ProductResult>>(() => RunFlightAsync(product, key)));
        try
        {
            return await flight.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ProductLoadException) when (before == ProductFreshnessState.Stale && loadedAt is { } at &&
                                           ProductFreshness.Evaluate(product, at, _time.GetUtcNow()) != ProductFreshnessState.TooStale)
        {
            // a failed reload serves the last result, marked stale, while MaxStaleness allows it
            return new ProductResult(key, at, true);
        }
    }

    private async Task<ProductResult> RunFlightAsync(DataProduct product, TableIdentifier key)
    {
        try
        {
            return await LoadAsync(product, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(key, out Lazy<Task<ProductResult>>? _);
        }
    }

    private ProductFreshnessState StateOf(DataProduct product, TableIdentifier key, out DateTimeOffset? loadedAt)
    {
        loadedAt = catalog.TryGet(key, out var meta) && meta is not null ? meta.LastRefreshedAt : null;
        return ProductFreshness.Evaluate(product, loadedAt, _time.GetUtcNow());
    }

    /// <summary>The <c>onDemand</c> products this process served since its start (the candidates of the idle drop; a result nobody reads here is not tracked).</summary>
    public IReadOnlyList<(TenantId Tenant, string Schema, string Name)> TrackedOnDemand() => [.. _tracked.Values];

    /// <summary>Drops the results of <c>onDemand</c> products that no read touched for their idle period. Returns the number dropped.</summary>
    public async Task<int> DropIdleAsync(IReadOnlyList<DataProduct> candidates, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var dropped = 0;
        foreach (var product in candidates.Where(static p => p.Load is { Mode: LoadMode.OnDemand, IdleDropMinutes: > 0 }))
        {
            var key = ProductSource.CacheKey(product.Tenant, product.Schema, product.Name);
            var last = _lastAccess.TryGetValue(key, out var t) ? t : (catalog.TryGet(key, out var m) && m is not null ? m.LastRefreshedAt : now);
            if (now - last > TimeSpan.FromMinutes(product.Load.IdleDropMinutes) && catalog.Remove(key))
            {
                try
                {
                    await store.EvictAsync(key, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // one failing product never blocks the others; the type only, a store message can carry values
                    logger?.LogWarning("Dropping the idle result of data product {Product} failed ({ExceptionType}).", $"{product.Schema}.{product.Name}", ex.GetType().Name);
                    continue;
                }

                _lastAccess.TryRemove(key, out _);
                _tracked.TryRemove(key, out _);
                dropped++;
                await audit.RecordBestEffortAsync(Entry(AuditEventTypes.ProductIdleDropped, product, new { idleMinutes = product.Load.IdleDropMinutes }, now), ct).ConfigureAwait(false);
            }
        }

        return dropped;
    }

    private async Task SetStatusAsync(DataProduct current, DataProductStatus status, CancellationToken ct)
    {
        if (current.Status == status)
        {
            return;
        }

        var outcome = await products.ReplaceProductAsync(current with { Status = status, UpdatedAt = _time.GetUtcNow() }, current.Version, null, ct).ConfigureAwait(false);
        if (outcome.Status != ProductWriteStatus.Replaced)
        {
            logger?.LogInformation("The product changed while it was loaded; the load status was not recorded.");
            return;
        }

        await ReprojectAsync(outcome.Product ?? current with { Status = status }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Exposure follows <see cref="ProductFreshness.IsExposable"/>: a product that turned invalid (or stale beyond MaxStaleness) is deactivated
    /// in the catalog at once (the catalog write publishes a restrictive change immediately). A failure is logged: the read path denies on its own.
    /// </summary>
    private async Task ReprojectAsync(DataProduct product, CancellationToken ct)
    {
        if (catalogStore is null)
        {
            return;
        }

        _deactivated.TryRemove(ProductSource.CacheKey(product.Tenant, product.Schema, product.Name), out _);

        try
        {
            await catalogStore.PersistAsync(product, LoadedAt(product), ct).ConfigureAwait(false);
            await AuditReprojectAsync(product, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("The catalog object of data product {Product} could not be re-projected ({ExceptionType}).", $"{product.Schema}.{product.Name}", ex.GetType().Name);
        }
    }

    private Task AuditReprojectAsync(DataProduct product, CancellationToken ct) =>
        audit.RecordAsync(
            Entry(AuditEventTypes.ProductReprojected, product, new { exposed = ProductFreshness.IsExposable(product, LoadedAt(product), _time.GetUtcNow()), status = product.Status.ToString() }, _time.GetUtcNow()),
            ct);

    /// <summary>
    /// Deactivates the catalog object of stale products whose last result aged past <c>MaxStaleness</c> without any load attempt (no status
    /// transition would). Once per product until its next transition; a failure is retried on the next tick.
    /// </summary>
    public async Task ReprojectAgedAsync(IReadOnlyList<DataProduct> candidates, CancellationToken ct = default)
    {
        if (catalogStore is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var product in candidates)
        {
            var key = ProductSource.CacheKey(product.Tenant, product.Schema, product.Name);
            if (product.Status != DataProductStatus.Stale || ProductFreshness.IsExposable(product, LoadedAt(product), now) || !_deactivated.TryAdd(key, true))
            {
                continue;
            }

            try
            {
                await catalogStore.PersistAsync(product, LoadedAt(product), ct).ConfigureAwait(false);
                await AuditReprojectAsync(product, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _deactivated.TryRemove(key, out _);
                logger?.LogWarning("The catalog object of data product {Product} could not be re-projected ({ExceptionType}).", $"{product.Schema}.{product.Name}", ex.GetType().Name);
            }
        }
    }

    /// <summary>Records a failure of a worker tick for one product (best effort, code only).</summary>
    public Task AuditFailureAsync(DataProduct product, string code, CancellationToken ct = default) =>
        audit.RecordBestEffortAsync(Entry(AuditEventTypes.ProductLoadFailed, product, new { code }, _time.GetUtcNow()), ct);

    private AuditLogEntry Entry(string eventType, DataProduct product, object details, DateTimeOffset at) => new()
    {
        EventType = eventType,
        TenantId = product.Tenant,
        ActorSid = new Sid("system:product-load"),
        TargetTable = $"products.{product.Schema}.{product.Name}",
        Decision = "ALLOW",
        DetailsJson = JsonSerializer.Serialize(new { productId = product.Id, definitionHash = product.DefinitionHash, mode = product.Load.Mode.ToString(), detail = details }),
        OccurredAt = at
    };
}

/// <summary>Global caps of a data product load (0 = no cap): a product limit above a cap is lowered to the cap.</summary>
internal sealed record ProductLoadCaps(long MaxRows, long MaxBytes, int MaxRuntimeSeconds)
{
    public static ProductLoadCaps Default { get; } = new(10_000_000, 2L * 1024 * 1024 * 1024, 900);
}
