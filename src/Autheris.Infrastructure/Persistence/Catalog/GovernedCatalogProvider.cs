using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>
/// WP-X0c / CR-ADG-47: builds the compiler catalog from database metadata. Fail closed: a table is part of the result only when
/// the governance record, the data source, the dialect and the database metadata all agree. Every other case (load failure,
/// zero columns, a governance column missing in the database, a tenant column with a non-TEXT SQLite affinity) omits the table,
/// so the compiler rejects it as unknown. Entries are cached per data source and table for at most
/// <see cref="GovernedCatalogOptions.MaxCacheTtlSeconds"/> seconds; denials and failures are never cached. Every cache hit is
/// re-validated against a fingerprint of the security-relevant governance inputs (data source, provider, dialect, governed columns,
/// tenant column), so a governance change takes effect on the next compile and not only after the TTL.
/// </summary>
public sealed partial class GovernedCatalogProvider : IGovernedCatalogProvider, IPhysicalKeyCatalog
{
    private readonly IGovernanceRepository _governance;
    private readonly IOptions<GatewayOptions> _gateway;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(string DataSource, string Schema, string Table), CacheItem> _cache = new();

    private readonly record struct CacheItem(TableCatalogEntry Entry, DateTimeOffset ExpiresAt, string Fingerprint);

    internal ISqlConnectionFactory? ConnectionFactory { get; init; }
    internal IReadOnlyDictionary<TargetSqlDialect, IKeyIndexMetadataReader>? KeyReaders { get; init; }

    private ISqlConnectionFactory? _connectionFactory => ConnectionFactory;
    private IReadOnlyDictionary<TargetSqlDialect, IKeyIndexMetadataReader> _keyReaders =>
        KeyReaders ?? ImmutableDictionary<TargetSqlDialect, IKeyIndexMetadataReader>.Empty;

    public GovernedCatalogProvider(
        IGovernanceRepository governance,
        IOptions<GatewayOptions> gateway,
        GovernedCatalogOptions options,
        TimeProvider? time = null,
        ILogger<GovernedCatalogProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(governance);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _governance = governance;
        _gateway = gateway;
        _ttl = TimeSpan.FromSeconds(options.CacheTtlSeconds);
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GovernedCatalogProvider>.Instance;
    }

    public GovernedCatalogProvider(
        IGovernanceRepository governance,
        IOptions<GatewayOptions> gateway,
        ISqlConnectionFactory connectionFactory,
        object? _ignoredReaders,
        GovernedCatalogOptions options,
        TimeProvider? time = null,
        ILogger<GovernedCatalogProvider>? logger = null,
        IEnumerable<IKeyIndexMetadataReader>? keyReaders = null)
        : this(governance, gateway, options, time, logger)
    {
        ConnectionFactory = connectionFactory;
        KeyReaders = (keyReaders ?? []).ToDictionary(static r => r.Dialect);
    }

    /// <summary>Drops the cached entries of one data source, or of all when <paramref name="dataSourceName"/> is null (metadata reload).</summary>
    public void Invalidate(string? dataSourceName = null)
    {
        foreach (var key in _cache.Keys)
        {
            if (dataSourceName is null || string.Equals(key.DataSource, dataSourceName, StringComparison.OrdinalIgnoreCase))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    public async ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var dataSourceName = dataSource.LogicalName;
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSourceName);
        ArgumentNullException.ThrowIfNull(tables);

        var entries = new List<TableCatalogEntry>(tables.Count);
        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            var key = (dataSourceName.ToLowerInvariant(), table.Schema, table.TableName);
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > _time.GetUtcNow() &&
                await FingerprintMatchesAsync(dataSourceName, table, cached.Fingerprint, ct).ConfigureAwait(false))
            {
                entries.Add(cached.Entry);
                continue;
            }

            try
            {
                var (loaded, fingerprint) = await LoadAsync(dataSourceName, table, ct).ConfigureAwait(false);
                if (loaded is null)
                {
                    _cache.TryRemove(key, out _);
                    continue;
                }

                _cache[key] = new CacheItem(loaded, _time.GetUtcNow() + _ttl, fingerprint);
                entries.Add(loaded);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fail closed. Only classified (credential-free) messages and type/number diagnostics are logged: raw driver messages
                // can carry connection details.
                LogLoadFailure(dataSourceName, table, ex);
                _cache.TryRemove(key, out _);
                if (TrinoSqlEngine.CompilerHooks.IsDisabled(TrinoSqlEngine.SecurityControl.CatalogLoadFailureDenies))
                {
                    entries.Add(new TableCatalogEntry(new TableIdentity(table.Schema, table.TableName), ImmutableArray<CatalogColumn>.Empty, null, 0));
                }
            }
        }

        string defaultSchema = entries.Count > 0 && entries.All(e => e.Identity.Schema == entries[0].Identity.Schema)
            ? entries[0].Identity.Schema
            : string.Empty;
        return new InMemoryTableCatalog(entries, defaultSchema);
    }

    // A failing source is retried on every cache miss (once per table); the Warning is limited to once per data source and reason
    // within this window so a down source does not flood the log. Suppressed repeats are logged at Debug.
    private static readonly long WarnWindowMs = 30_000;
    private readonly ConcurrentDictionary<string, long> _lastLoadWarning = new(StringComparer.Ordinal);

    private LogLevel NextLoadFailureLevel(string key)
    {
        long now = Environment.TickCount64;
        if (_lastLoadWarning.TryGetValue(key, out long previous) && now - previous < WarnWindowMs)
        {
            return LogLevel.Debug;
        }

        _lastLoadWarning[key] = now;
        return LogLevel.Warning;
    }

    private void LogLoadFailure(string dataSourceName, TableIdentifier table, Exception ex)
    {
        ISqlSourceDiagnosticException? diagnostic = null;
        var types = new List<string>();
        Microsoft.Data.SqlClient.SqlException? sqlException = null;
        for (var current = ex; current is not null; current = current.InnerException)
        {
            types.Add(current.GetType().Name);
            diagnostic ??= current as ISqlSourceDiagnosticException;
            sqlException ??= current as Microsoft.Data.SqlClient.SqlException;
        }

        if (diagnostic is SqlSourceLoginBlockedException blocked)
        {
            // The breaker already logged once at Warning when it opened; a blocked retry per query would only spam the log.
            _logger.LogDebug("Catalog metadata load skipped for data source {DataSource}, table {Table}: login breaker open ({Remaining:F0} s remaining); the table is denied.",
                dataSourceName, table.ToQualifiedName(), blocked.Remaining.TotalSeconds);
            return;
        }

        if (diagnostic is Exception diagnosticException)
        {
            string fields = string.Join(", ", diagnostic.DiagnosticFields.Select(static f => $"{f.Key}={f.Value}"));
            _logger.Log(NextLoadFailureLevel($"{dataSourceName}|{diagnostic.CategoryName}"),
                "Catalog metadata load failed for data source {DataSource}, table {Table}: {Message} [Category={Category}; {Fields}]; the table is denied.",
                dataSourceName, table.ToQualifiedName(), diagnosticException.Message, diagnostic.CategoryName, fields);
            return;
        }

        string chain = string.Join(" -> ", types);
        _logger.Log(NextLoadFailureLevel($"{dataSourceName}|{chain}"),
            "Catalog metadata load failed for data source {DataSource}, table {Table} ({ExceptionTypes}{SqlDetails}); the table is denied.",
            dataSourceName, table.ToQualifiedName(), chain,
            sqlException is null ? string.Empty : $"; SqlNumber={sqlException.Number}, SqlState={sqlException.State}, SqlClass={sqlException.Class}");
    }

    private DataSourceConnectionOptions? FindConnection(string dataSourceName) =>
        _gateway.Value.DataSources?.Connections is { } connections && connections.TryGetValue(dataSourceName, out var options) ? options : null;

    // A cache hit is only valid while the governance record still yields the same fingerprint. Any failure to read it is a miss
    // (the reload then fails closed on its own).
    private async Task<bool> FingerprintMatchesAsync(string dataSourceName, TableIdentifier table, string expected, CancellationToken ct)
    {
        try
        {
            var metadata = await _governance.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
            return metadata is not null &&
                   FindConnection(dataSourceName) is { } connection &&
                   string.Equals(Fingerprint(connection, metadata), expected, StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Fingerprint(DataSourceConnectionOptions connection, TableMetadata metadata)
    {
        var columns = metadata.Columns
            .Select(static c => $"{c.ColumnName.ToLowerInvariant()}:{c.DataType.ToLowerInvariant()}:{c.Collation?.ToLowerInvariant()}")
            .Order(StringComparer.Ordinal);
        return string.Join('\u001f', new[]
        {
            connection.Provider ?? string.Empty,
            metadata.Table.SourceName.ToLowerInvariant(),
            ((int)metadata.DataSourceType).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)metadata.Dialect).ToString(System.Globalization.CultureInfo.InvariantCulture),
            metadata.TenantColumnName?.ToLowerInvariant() ?? string.Empty,
            string.Join('\u001e', columns)
        });
    }

    // Every silent denial used to be invisible in the log (issue: NTLM catalog diagnostics); the reason names only the data source,
    // table, provider and dialect, never connection details.
    private void LogAdmissionDenied(string dataSourceName, TableIdentifier table, string reason) =>
        _logger.LogWarning("Catalog admission denied for data source {DataSource}, table {Table}: {Reason} The table is denied.",
            dataSourceName, table.ToQualifiedName(), reason);

    private async Task<(TableCatalogEntry? Entry, string Fingerprint)> LoadAsync(
        string dataSourceName, TableIdentifier table, CancellationToken ct)
    {
        var connectionOptions = FindConnection(dataSourceName);
        if (connectionOptions is null)
        {
            LogAdmissionDenied(dataSourceName, table, $"The data source '{dataSourceName}' has no configured connection.");
            return (null, string.Empty);
        }

        if (!DataSourceProvider.TryResolveDialect(connectionOptions.Provider, out var dialect) ||
            !SqlDialectMapper.IsExecutable(dialect))
        {
            LogAdmissionDenied(dataSourceName, table, $"The provider '{connectionOptions.Provider}' of the data source is not supported or not executable.");
            return (null, string.Empty);
        }

        var metadata = await _governance.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        if (metadata is null)
        {
            LogAdmissionDenied(dataSourceName, table, "The table is not registered in the governance catalog.");
            return (null, string.Empty);
        }

        if (metadata.DataSourceType != DataSourceType.Sql ||
            !string.Equals(metadata.Table.SourceName, dataSourceName, StringComparison.OrdinalIgnoreCase) ||
            metadata.Dialect != dialect)
        {
            LogAdmissionDenied(dataSourceName, table,
                $"The catalog entry (type {metadata.DataSourceType}, source '{metadata.Table.SourceName}', dialect {metadata.Dialect}) does not match the configured connection (dialect {dialect}).");
            return (null, string.Empty);
        }

        if (metadata.Columns.Count == 0)
        {
            LogAdmissionDenied(dataSourceName, table, "The table has no columns defined in the governance catalog.");
            return (null, string.Empty);
        }

        string? tenantColumn = null;
        if (metadata.TenantColumnName is { } governedTenant)
        {
            var tenantCol = metadata.Columns.FirstOrDefault(c =>
                string.Equals(c.ColumnName, governedTenant, StringComparison.OrdinalIgnoreCase));

            if (tenantCol is null)
            {
                LogAdmissionDenied(dataSourceName, table, $"The tenant column '{governedTenant}' is not present in declared columns.");
                return (null, string.Empty);
            }

            // With NUMERIC or INTEGER affinity '007' and '7' compare equal: such a column cannot isolate tenants.
            if (dialect == DatabaseDialect.Sqlite && SqliteTypeAffinity.Of(tenantCol.DataType) != SqliteAffinity.TextAffinity)
            {
                LogAdmissionDenied(dataSourceName, table, $"The tenant column '{governedTenant}' in SQLite must have text affinity.");
                return (null, string.Empty);
            }

            tenantColumn = tenantCol.ColumnName;
        }

        var identity = new TableIdentity(metadata.Table.SchemaName, metadata.Table.TableName);
        var catalogColumns = metadata.Columns
            .Select(static c => new CatalogColumn(c.ColumnName, c.DataType, c.Collation))
            .ToImmutableArray();

        var entry = new TableCatalogEntry(
            identity,
            catalogColumns,
            tenantColumn,
            metadata.RequiredFilterColumns,
            CatalogColumnVersion.Compute(metadata.Columns));
        return (entry, Fingerprint(connectionOptions, metadata));
    }
}
