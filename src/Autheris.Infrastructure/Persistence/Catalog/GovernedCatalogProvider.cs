using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data.Common;
using Autheris.Application.Interfaces;
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
public sealed class GovernedCatalogProvider : IGovernedCatalogProvider
{
    private readonly IGovernanceRepository _governance;
    private readonly IOptions<GatewayOptions> _gateway;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IReadOnlyDictionary<TargetSqlDialect, IColumnMetadataReader> _readers;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(string DataSource, string Schema, string Table), CacheItem> _cache = new();

    private readonly record struct CacheItem(TableCatalogEntry Entry, DateTimeOffset ExpiresAt, string Fingerprint);

    public GovernedCatalogProvider(
        IGovernanceRepository governance,
        IOptions<GatewayOptions> gateway,
        ISqlConnectionFactory connectionFactory,
        IEnumerable<IColumnMetadataReader> readers,
        GovernedCatalogOptions options,
        TimeProvider? time = null,
        ILogger<GovernedCatalogProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(governance);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _governance = governance;
        _gateway = gateway;
        _connectionFactory = connectionFactory;
        _readers = readers.ToDictionary(static r => r.Dialect);
        _ttl = TimeSpan.FromSeconds(options.CacheTtlSeconds);
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GovernedCatalogProvider>.Instance;
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

    public async ValueTask<ITableCatalog> GetCatalogAsync(string dataSourceName, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSourceName);
        ArgumentNullException.ThrowIfNull(tables);

        var entries = new List<TableCatalogEntry>(tables.Count);
        DbConnection? connection = null;
        try
        {
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
                    var (loaded, fingerprint) = await LoadAsync(dataSourceName, table, async () => connection ??= await OpenAsync(dataSourceName, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
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
                    // Fail closed. Only the exception type is logged: driver messages can carry connection details.
                    _logger.LogWarning("Catalog metadata load failed for data source {DataSource}, table {Table} ({ExceptionType}); the table is denied.",
                        dataSourceName, table.ToQualifiedName(), ex.GetType().Name);
                    _cache.TryRemove(key, out _);
#if DEBUG
                    if (TrinoSqlEngine.CompilerTestSeams.IsDisabled(TrinoSqlEngine.SecurityControl.CatalogLoadFailureDenies))
                    {
                        entries.Add(new TableCatalogEntry(new TableIdentity(table.Schema, table.TableName), ImmutableArray<CatalogColumn>.Empty, null, 0));
                    }
#endif
                }
            }
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        string defaultSchema = entries.Count > 0 && entries.All(e => e.Identity.Schema == entries[0].Identity.Schema)
            ? entries[0].Identity.Schema
            : string.Empty;
        return new InMemoryTableCatalog(entries, defaultSchema);
    }

    private async Task<DbConnection> OpenAsync(string dataSourceName, CancellationToken ct)
    {
        var options = FindConnection(dataSourceName)
            ?? throw new InvalidOperationException($"The data source '{dataSourceName}' has no configured connection.");
        return await _connectionFactory.CreateOpenConnectionAsync(options, ct).ConfigureAwait(false);
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
        var columns = metadata.Columns.Select(static c => c.ColumnName.ToLowerInvariant()).Order(StringComparer.Ordinal);
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

    private async Task<(TableCatalogEntry? Entry, string Fingerprint)> LoadAsync(
        string dataSourceName, TableIdentifier table, Func<Task<DbConnection>> open, CancellationToken ct)
    {
        var connectionOptions = FindConnection(dataSourceName);
        if (connectionOptions is null || !DataSourceProvider.TryResolveDialect(connectionOptions.Provider, out var dialect))
        {
            return (null, string.Empty);
        }

        var metadata = await _governance.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        if (metadata is null ||
            metadata.DataSourceType != DataSourceType.Sql ||
            !string.Equals(metadata.Table.SourceName, dataSourceName, StringComparison.OrdinalIgnoreCase) ||
            metadata.Dialect != dialect)
        {
            return (null, string.Empty);
        }

        if (!SqlDialectMapper.TryToTargetDialect(dialect, out var target) || !_readers.TryGetValue(target, out var reader))
        {
            return (null, string.Empty);
        }

        // Databricks is mapped and has a column reader, but its governed AST path is not enabled before X1-DBX: the table is denied
        // explicitly (fail closed), so enabling the reader or the mapping alone can never admit a Databricks source.
        if (target == TargetSqlDialect.Databricks)
        {
            _logger.LogWarning("Catalog admission denied for data source {DataSource}, table {Table}: the Databricks dialect is not enabled for the governed SQL compiler (X1-DBX).",
                dataSourceName, table.ToQualifiedName());
            return (null, string.Empty);
        }

        var identity = new TableIdentity(metadata.Table.SchemaName, metadata.Table.TableName);
        var connection = await open().ConfigureAwait(false);
        var dbColumns = new List<DbColumnMetadata>();
        await foreach (var column in reader.ReadAsync(connection, identity, ct).ConfigureAwait(false))
        {
            dbColumns.Add(column);
        }

        if (dbColumns.Count == 0)
        {
            return (null, string.Empty);   // a table without columns is a table the database does not know (or a wrong spelling)
        }

        var exposed = new List<DbColumnMetadata>(metadata.Columns.Count);
        foreach (var governed in metadata.Columns)
        {
            if (!TryMatch(dbColumns, governed.ColumnName, out var match))
            {
                return (null, string.Empty);   // governance column missing (or ambiguous) in the database
            }

            if (!exposed.Contains(match))
            {
                exposed.Add(match);
            }
        }

        if (exposed.Count == 0)
        {
            return (null, string.Empty);
        }

        // Keep the database column order, so the version does not depend on the order of the governance record.
        exposed.Sort((a, b) => dbColumns.IndexOf(a).CompareTo(dbColumns.IndexOf(b)));

        string? tenantColumn = null;
        if (metadata.TenantColumnName is { } governedTenant)
        {
            if (!TryMatch(exposed, governedTenant, out var tenant))
            {
                return (null, string.Empty);
            }

            // With NUMERIC or INTEGER affinity '007' and '7' compare equal: such a column cannot isolate tenants (SEC-X1-09).
            if (target == TargetSqlDialect.Sqlite && SqliteTypeAffinity.Of(tenant.NativeType) != SqliteAffinity.TextAffinity)
            {
                return (null, string.Empty);
            }

            tenantColumn = tenant.Name;
        }

        var entry = new TableCatalogEntry(
            identity,
            exposed.Select(static c => new CatalogColumn(c.Name, c.NativeType, c.Collation)).ToImmutableArray(),
            tenantColumn,
            CatalogColumnVersion.Compute(exposed));
        return (entry, Fingerprint(connectionOptions, metadata));
    }

    // Exact spelling wins; otherwise a single case-insensitive match. Two candidates that differ only by case are ambiguous.
    private static bool TryMatch(List<DbColumnMetadata> columns, string name, out DbColumnMetadata match)
    {
        match = default;
        bool found = false;
        foreach (var c in columns)
        {
            if (string.Equals(c.Name, name, StringComparison.Ordinal))
            {
                match = c;
                return true;
            }
        }

        foreach (var c in columns)
        {
            if (!string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found)
            {
                return false;
            }

            match = c;
            found = true;
        }

        return found;
    }
}
