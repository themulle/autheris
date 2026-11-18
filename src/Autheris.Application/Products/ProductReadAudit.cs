namespace Autheris.Application.Products;

using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>The time of the last successful stage-1 load of a product.</summary>
public interface IProductRefreshTimes
{
    DateTimeOffset? LoadedAt(DataProduct product);
}

/// <summary>The refresh times of a host without stage 1 loading: no product has a result.</summary>
public sealed class NoProductRefreshTimes : IProductRefreshTimes
{
    public static readonly NoProductRefreshTimes Instance = new();

    public DateTimeOffset? LoadedAt(DataProduct product) => null;
}

/// <summary>Audit of a product read (stage 2), written before the rows leave the reader.</summary>
public interface IProductReadAudit
{
    /// <summary>Records the read or throws; the caller aborts the read when the audit is unavailable.</summary>
    Task RecordReadAsync(ClaimsPrincipal principal, TenantId tenant, string schema, string name, int rowCount, CancellationToken ct = default);
}

/// <summary>
/// Writes the read audit of a product: product id, definition hash, load mode, result age and row count; never values. A missing product
/// fails the read (the reader had just served it), so an unaudited read cannot happen.
/// </summary>
public sealed class ProductReadAudit(
    IDataProductRepository products,
    IProductRefreshTimes refreshTimes,
    IAuditWriter audit,
    ProductUsageTracker? usage = null,
    TimeProvider? timeProvider = null) : IProductReadAudit
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task RecordReadAsync(ClaimsPrincipal principal, TenantId tenant, string schema, string name, int rowCount, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var product = await products.GetProductAsync(tenant, schema, name, ct).ConfigureAwait(false)
            ?? throw new ProductLoadException(ProductErrorCodes.Unavailable);
        var now = _time.GetUtcNow();
        var loadedAt = refreshTimes.LoadedAt(product);
        var actor = SecurityPrincipalContext.FromPrincipal(principal, tenant).UserSid;
        await audit.RecordRequiredAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.ProductRead,
            TenantId = tenant,
            ActorSid = actor,
            TargetTable = $"products.{product.Schema}.{product.Name}",
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new
            {
                productId = product.Id,
                definitionHash = product.DefinitionHash,
                loadMode = product.Load.Mode.ToString(),
                resultAgeSeconds = loadedAt is { } at ? (long?)Math.Max(0, (long)(now - at).TotalSeconds) : null,
                rowCount
            }),
            OccurredAt = now
        }, ct).ConfigureAwait(false);
        usage?.RecordRead(tenant, product.Id, rowCount);
    }
}
