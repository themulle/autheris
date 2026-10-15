namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Everything the poller needs about one activated table. Built only from the declared table definition.</summary>
public sealed record CdcActivation(
    CdcTableSettings Settings,
    CdcStreamDescriptor Stream,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<string> Columns,
    string? TenantColumn,
    string? OwnerTenantId,
    DataSourceConnectionOptions Source);

/// <summary>Outcome of an activation attempt: an <see cref="Activation"/> or the stable <see cref="CdcActivationCodes"/> code of the first failed check.</summary>
public sealed record CdcActivationResult(CdcActivation? Activation, string? Code)
{
    public static CdcActivationResult Ok(CdcActivation activation) => new(activation, null);

    public static CdcActivationResult Fail(string code) => new(null, code);

    public bool IsActive => Activation is not null;
}

public interface ICdcTableActivator
{
    /// <summary>Runs (or reuses) the activation checks of one tracked table. Transient source errors propagate; they are not activation codes.</summary>
    Task<CdcActivationResult> EnsureActiveAsync(CdcTableSettings settings, CancellationToken ct = default);

    /// <summary>Forgets every cached activation (options or catalog change).</summary>
    void Invalidate();
}

/// <summary>
/// PLAN-CDC-99 D-A3 / FR-P2: activates one tracked table. Checks run in a fixed order, each with a stable code, the first failure wins
/// and is stored on the stream (<c>activation_code</c>). The primary key is the DECLARED one (never read from the source catalog); the
/// change table's key columns are compared with it. Needs only <c>SELECT</c> and <c>VIEW CHANGE TRACKING</c> on the source.
/// </summary>
public sealed class CdcTableActivator : ICdcTableActivator
{
    public const string StreamSource = "mssql_ct";

    private static readonly TimeSpan CatalogCheckInterval = TimeSpan.FromSeconds(30);

    private static readonly Counter<long> ActivationFailed = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_cdc_stream_activation_failed",
        description: "CDC stream activation attempts that failed, by stable activation code");

    private readonly ITableMetadataRepository _metadata;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ICdcStreamStore _store;
    private readonly ICdcStreamCrypto _crypto;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly ICatalogVersionProvider? _catalogVersion;
    private readonly TimeProvider _time;
    private readonly ILogger<CdcTableActivator> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public CdcTableActivator(
        ITableMetadataRepository metadata,
        ISqlConnectionFactory connectionFactory,
        ICdcStreamStore store,
        ICdcStreamCrypto crypto,
        IOptionsMonitor<GatewayOptions> options,
        ILogger<CdcTableActivator> logger,
        ICatalogVersionProvider? catalogVersion = null,
        TimeProvider? timeProvider = null)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _catalogVersion = catalogVersion;
        _time = timeProvider ?? TimeProvider.System;
    }

    public void Invalidate() => _cache.Clear();

    public async Task<CdcActivationResult> EnsureActiveAsync(CdcTableSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MssqlChangeTrackingPoller.ValidateSqlIdentifier(settings.Table.Schema, nameof(settings.Table.Schema));
        MssqlChangeTrackingPoller.ValidateSqlIdentifier(settings.Table.TableName, nameof(settings.Table.TableName));

        var key = settings.PartitionKey;
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(key, out var cached) && cached.Settings == settings)
        {
            if (_catalogVersion is null || now - cached.VersionCheckedAt < CatalogCheckInterval)
            {
                return await RefreshStreamAsync(cached.Result, ct).ConfigureAwait(false);
            }

            var current = await _catalogVersion.GetVersionAsync(ct).ConfigureAwait(false);
            if (string.Equals(current, cached.CatalogVersion, StringComparison.Ordinal))
            {
                _cache[key] = cached with { VersionCheckedAt = now };
                return await RefreshStreamAsync(cached.Result, ct).ConfigureAwait(false);
            }
        }

        var catalogVersion = _catalogVersion is null ? null : await _catalogVersion.GetVersionAsync(ct).ConfigureAwait(false);
        var result = await ActivateAsync(settings, ct).ConfigureAwait(false);
        if (result.IsActive)
        {
            _cache[key] = new CacheEntry(settings, result, catalogVersion, now);
        }
        else
        {
            _cache.TryRemove(key, out _);
        }

        return result;
    }

    /// <summary>A cached activation is reused, but the stream descriptor (head, watermark, status) is always fresh; a halted stream deactivates.</summary>
    private async Task<CdcActivationResult> RefreshStreamAsync(CdcActivationResult cached, CancellationToken ct)
    {
        var activation = cached.Activation!;
        var fresh = await _store.GetStreamAsync(activation.Stream.Id, ct).ConfigureAwait(false);
        if (fresh is null)
        {
            _cache.TryRemove(activation.Settings.PartitionKey, out _);
            return await ActivateAsync(activation.Settings, ct).ConfigureAwait(false);
        }

        if (fresh.Status == CdcStreamStatus.Halted)
        {
            return CdcActivationResult.Fail(CdcActivationCodes.WatermarkExpired);
        }

        return CdcActivationResult.Ok(activation with { Stream = fresh });
    }

    private async Task<CdcActivationResult> ActivateAsync(CdcTableSettings settings, CancellationToken ct)
    {
        var table = settings.Table;
        var descriptor = await _store.EnsureStreamAsync(StreamSource, table, ct).ConfigureAwait(false);
        if (descriptor.Status == CdcStreamStatus.Halted)
        {
            // Only an administrative resync clears a halt; activation never overrides it.
            return CdcActivationResult.Fail(CdcActivationCodes.WatermarkExpired);
        }

        var outcome = await RunChecksAsync(settings, descriptor, ct).ConfigureAwait(false);
        if (outcome.IsActive)
        {
            if (descriptor.Status != CdcStreamStatus.Active || descriptor.ActivationCode is not null)
            {
                await _store.SetStatusAsync(descriptor.Id, CdcStreamStatus.Active, null, ct).ConfigureAwait(false);
            }

            return outcome;
        }

        ActivationFailed.Add(1, new KeyValuePair<string, object?>("code", outcome.Code));
        if (descriptor.Status != CdcStreamStatus.ActivationFailed || descriptor.ActivationCode != outcome.Code)
        {
            await _store.SetStatusAsync(descriptor.Id, CdcStreamStatus.ActivationFailed, outcome.Code, ct).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<CdcActivationResult> RunChecksAsync(CdcTableSettings settings, CdcStreamDescriptor descriptor, CancellationToken ct)
    {
        var table = settings.Table;
        var gateway = _options.CurrentValue;

        // 1. catalog metadata, active, SQL Server, resolvable source connection (P-1)
        TableMetadata? metadata;
        try
        {
            metadata = await _metadata.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        }
        catch (TableMetadataUnavailableException)
        {
            return CdcActivationResult.Fail(CdcActivationCodes.TableUnavailable);
        }

        if (metadata is not { Table.IsActive: true, DataSourceType: DataSourceType.Sql } || metadata.Dialect != DatabaseDialect.SqlServer)
        {
            return CdcActivationResult.Fail(CdcActivationCodes.TableUnavailable);
        }

        var sourceName = string.IsNullOrWhiteSpace(settings.DataSource) ? metadata.Table.SourceName : settings.DataSource;
        if (string.IsNullOrWhiteSpace(sourceName)
            || !gateway.DataSources.Connections.TryGetValue(sourceName, out var source)
            || !DataSourceProvider.Is(source.Provider, DatabaseDialect.SqlServer)
            || string.IsNullOrWhiteSpace(source.ConnectionString))
        {
            return CdcActivationResult.Fail(CdcActivationCodes.TableUnavailable);
        }

        // 2. declared primary key and catalogued columns
        var primaryKey = metadata.PrimaryKeyColumns;
        if (primaryKey.Count == 0)
        {
            return CdcActivationResult.Fail(CdcActivationCodes.NoDeclaredPk);
        }

        var columns = metadata.Columns.Select(c => c.ColumnName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            foreach (var name in primaryKey.Concat(columns))
            {
                MssqlChangeTrackingPoller.ValidateSqlIdentifier(name, nameof(name));
            }
        }
        catch (ArgumentException)
        {
            return CdcActivationResult.Fail(CdcActivationCodes.NoDeclaredPk);
        }

        // 3. tenant rule (same rules as the read path)
        string? tenantColumn;
        try
        {
            tenantColumn = TableMetadata.RequireTenantColumnOrThrow(metadata, gateway.DataSources.RequireTenantColumn, gateway.DataSources.TenantColumnExemptTables);
        }
        catch (SecurityException)
        {
            return CdcActivationResult.Fail(CdcActivationCodes.TenantColumnMissing);
        }

        // 4..6 need the source; a login without VIEW CHANGE TRACKING / SELECT fails with a stable code, never as a transient error
        try
        {
            await using var conn = await _connectionFactory.CreateOpenConnectionAsync(source, ct).ConfigureAwait(false);
            var minValid = await CdcSourceQueries.GetMinValidVersionAsync(conn, null, table, settings.QueryTimeoutSeconds, ct).ConfigureAwait(false);
            if (minValid is null)
            {
                return CdcActivationResult.Fail(CdcActivationCodes.TrackingDisabled);
            }

            var keyColumns = await CdcSourceQueries.ReadChangeTableKeyColumnsAsync(conn, table, minValid.Value, settings.QueryTimeoutSeconds, ct).ConfigureAwait(false);
            if (!keyColumns.SequenceEqual(primaryKey, StringComparer.OrdinalIgnoreCase))
            {
                return CdcActivationResult.Fail(CdcActivationCodes.PkMismatch);
            }

            if (!settings.AllowNonSnapshotRead
                && !await CdcSourceQueries.ProbeSnapshotIsolationAsync(conn, table, settings.QueryTimeoutSeconds, ct).ConfigureAwait(false))
            {
                return CdcActivationResult.Fail(CdcActivationCodes.SnapshotIsolationOff);
            }
        }
        catch (DbException ex) when (CdcSourceQueries.IsPermissionDenied(ex))
        {
            return CdcActivationResult.Fail(CdcActivationCodes.TrackingDisabled);
        }

        // 7. stream keys
        try
        {
            await ProbeKeysAsync(descriptor.Id, ct).ConfigureAwait(false);
        }
        catch (CdcStreamKeyUnavailableException)
        {
            return CdcActivationResult.Fail(CdcActivationCodes.KeyUnavailable);
        }

        _logger.LogInformation("CDC stream of table {Table} activated.", table);
        return CdcActivationResult.Ok(new CdcActivation(settings, descriptor, primaryKey.ToList(), columns, tenantColumn, metadata.OwnerTenantId, source));
    }

    private async Task ProbeKeysAsync(CdcStreamId id, CancellationToken ct)
    {
        var fingerprint = await _crypto.FingerprintAsync(id, ["activation-probe"], ct).ConfigureAwait(false);
        await _crypto.ProtectEventAsync(id, 0, CdcStreamEventKind.Resync, CdcTenantScope.Shared, null, null, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        await _crypto.ProtectImageAsync(id, fingerprint, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    private sealed record CacheEntry(CdcTableSettings Settings, CdcActivationResult Result, string? CatalogVersion, DateTimeOffset VersionCheckedAt);
}
