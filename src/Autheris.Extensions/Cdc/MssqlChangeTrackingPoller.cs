namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Domain.Interfaces;

/// <summary>
/// Polls Microsoft SQL Server Change Tracking (CHANGETABLE) and dispatches CdcEvents to ICdcEventChannel.
/// Zero-Kafka, Zero-Storage Realtime Ingestion Engine (F-CDC-02).
/// </summary>
public sealed class MssqlChangeTrackingPoller : IMssqlChangeTrackingPoller
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IMssqlWatermarkStore _watermarkStore;
    private readonly ICdcEventChannel _eventChannel;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly ILogger<MssqlChangeTrackingPoller> _logger;
    private readonly ConcurrentDictionary<TableIdentifier, IReadOnlyList<string>> _pkCache = new();

    public MssqlChangeTrackingPoller(
        ISqlConnectionFactory connectionFactory,
        IMssqlWatermarkStore watermarkStore,
        ICdcEventChannel eventChannel,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<MssqlChangeTrackingPoller> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _watermarkStore = watermarkStore ?? throw new ArgumentNullException(nameof(watermarkStore));
        _eventChannel = eventChannel ?? throw new ArgumentNullException(nameof(eventChannel));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private DataSourceConnectionOptions GetDataSourceOptions()
    {
        var cdcOptions = _gatewayOptions.Value.MssqlChangeTracking;
        var connStr = !string.IsNullOrWhiteSpace(cdcOptions.ConnectionString)
            ? cdcOptions.ConnectionString
            : _gatewayOptions.Value.DataSources.Connections.Values.FirstOrDefault(s =>
                DataSourceProvider.Is(s.Provider, DatabaseDialect.SqlServer))?.ConnectionString ?? string.Empty;

        return new DataSourceConnectionOptions
        {
            Provider = "sqlserver",
            ConnectionString = connStr
        };
    }

    private static readonly System.Text.RegularExpressions.Regex IdentifierRegex =
        new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static void ValidateSqlIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !IdentifierRegex.IsMatch(identifier))
        {
            throw new ArgumentException($"Security error: Invalid SQL identifier '{identifier}'. Only alphanumeric characters and underscores are allowed.", paramName);
        }
    }

    public async Task<long?> GetCurrentDbVersionAsync(CancellationToken ct = default)
    {
        var dsOptions = GetDataSourceOptions();
        if (string.IsNullOrWhiteSpace(dsOptions.ConnectionString))
        {
            return null;
        }

        try
        {
            await using var conn = await _connectionFactory.CreateOpenConnectionAsync(dsOptions, ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION();";
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (result != null && result != DBNull.Value && long.TryParse(result.ToString(), out var version))
            {
                return version;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query CHANGE_TRACKING_CURRENT_VERSION from MSSQL.");
            return null;
        }
    }

    private static readonly Prometheus.Counter WatermarkExpiredTotal = Prometheus.Metrics.CreateCounter(
        "autheris_cdc_watermark_expired_total", "Number of polls aborted because the stored CDC watermark is older than the minimum valid change tracking version");

    /// <summary>
    /// Polls one table and publishes its changes. Delivery is at-least-once: events are published first and the watermark is
    /// committed afterwards (fenced by <paramref name="fencingToken"/>), so a crash between the two re-publishes the batch with the
    /// same deterministic <c>EventId</c>; consumers deduplicate on it. Only complete change versions are published and committed
    /// (a batch cut inside one SYS_CHANGE_VERSION never advances past that version).
    /// </summary>
    public async Task<int> PollTableChangesAsync(TableIdentifier table, long fencingToken, CancellationToken ct = default)
    {
        ValidateSqlIdentifier(table.Schema, nameof(table.Schema));
        ValidateSqlIdentifier(table.TableName, nameof(table.TableName));

        var dsOptions = GetDataSourceOptions();
        if (string.IsNullOrWhiteSpace(dsOptions.ConnectionString))
        {
            return 0;
        }

        var rawBatch = _gatewayOptions.Value.MssqlChangeTracking.BatchSize <= 0 ? 500 : _gatewayOptions.Value.MssqlChangeTracking.BatchSize;
        var batchSize = Math.Clamp(rawBatch, 1, 5000);

        var stored = await _watermarkStore.GetWatermarkAsync(table, ct).ConfigureAwait(false);

        try
        {
            await using var conn = await _connectionFactory.CreateOpenConnectionAsync(dsOptions, ct).ConfigureAwait(false);

            // 1. Resolve primary key columns; a table without a readable primary key cannot be tracked safely.
            var pkColumns = await GetPrimaryKeyColumnsAsync(conn, table, ct).ConfigureAwait(false);
            foreach (var pk in pkColumns)
            {
                ValidateSqlIdentifier(pk, nameof(pk));
            }

            // 2. First run: start at the current version instead of replaying the whole change history.
            if (stored is null)
            {
                var current = await GetCurrentVersionAsync(conn, ct).ConfigureAwait(false)
                    ?? throw new CdcSourceConfigurationException($"Change tracking is not enabled for the database of table '{table}'.");
                await CommitAsync(table, current, fencingToken, ct).ConfigureAwait(false);
                _logger.LogInformation("CDC for table {Table} starts at the current change tracking version {Version}; no history is replayed.", table, current);
                return 0;
            }

            var lastVersion = stored.Value;

            // 3. A watermark behind the retention horizon means lost changes: fail instead of silently skipping them.
            var minValidVersion = await GetMinValidVersionAsync(conn, table, ct).ConfigureAwait(false)
                ?? throw new CdcSourceConfigurationException($"Change tracking is not enabled for table '{table}' (no minimum valid version).");
            if (lastVersion < minValidVersion)
            {
                WatermarkExpiredTotal.Inc();
                _logger.LogError(
                    "MSSQL Change Tracking watermark {LastVersion} for table {Table} is older than min valid version {MinValidVersion}. Manual recovery is required (delete the row of the table in autheris_cdc_watermarks to restart at the current version).",
                    lastVersion, table, minValidVersion);
                throw new CdcWatermarkExpiredException(table, lastVersion, minValidVersion);
            }

            // 4. Read one batch and keep only complete change versions.
            var changes = await ReadChangesAsync(conn, table, pkColumns, lastVersion, batchSize, exactVersion: null, ct).ConfigureAwait(false);
            if (changes.Count == 0)
            {
                return 0;
            }

            if (changes.Count >= batchSize)
            {
                var cutVersion = changes[^1].Version;
                var complete = changes.TakeWhile(c => c.Version < cutVersion).ToList();
                if (complete.Count == 0)
                {
                    // The whole batch is one version, which may continue beyond it: fetch that version completely.
                    changes = await ReadChangesAsync(conn, table, pkColumns, lastVersion, batchSize, cutVersion, ct).ConfigureAwait(false);
                }
                else
                {
                    changes = complete;
                }
            }

            // Probe the fence before publishing: a writer that lost its lease must not publish (and, retried every cycle,
            // republish) a batch it can never commit.
            await CommitAsync(table, lastVersion, fencingToken, ct).ConfigureAwait(false);

            foreach (var change in changes)
            {
                await _eventChannel.PublishAsync(change.Event, ct).ConfigureAwait(false);
            }

            var maxVersion = changes.Max(c => c.Version);
            if (maxVersion > lastVersion)
            {
                await CommitAsync(table, maxVersion, fencingToken, ct).ConfigureAwait(false);
            }

            return changes.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                      and not FencingTokenRejectedException
                                      and not CdcWatermarkExpiredException
                                      and not CdcSourceConfigurationException)
        {
            _logger.LogError(ex, "Error while polling CHANGETABLE for {Table} at version {Version}.", table, stored);
            return 0;
        }
    }

    private async Task CommitAsync(TableIdentifier table, long version, long fencingToken, CancellationToken ct)
    {
        if (!await _watermarkStore.TryAdvanceWatermarkAsync(table, version, fencingToken, ct).ConfigureAwait(false))
        {
            throw new FencingTokenRejectedException($"{table.Domain}.{table.Schema}.{table.TableName}", fencingToken);
        }
    }

    private async Task<long?> GetCurrentVersionAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION();";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result != null && result != DBNull.Value && long.TryParse(result.ToString(), out var version) ? version : null;
    }

    private async Task<List<(long Version, CdcEvent Event)>> ReadChangesAsync(
        DbConnection conn,
        TableIdentifier table,
        IReadOnlyList<string> pkColumns,
        long lastVersion,
        int batchSize,
        long? exactVersion,
        CancellationToken ct)
    {
        var joinPredicates = string.Join(" AND ", pkColumns.Select(pk => $"T.[{pk}] = CT.[{pk}]"));
        var top = exactVersion is null ? "TOP (@batchSize)" : string.Empty;
        var versionFilter = exactVersion is null ? string.Empty : "WHERE CT.SYS_CHANGE_VERSION = @version";
        var sql = $"""
            SELECT {top}
                CT.SYS_CHANGE_VERSION,
                CT.SYS_CHANGE_OPERATION,
                {string.Join(", ", pkColumns.Select(pk => $"CT.[{pk}] AS [PK_{pk}]"))},
                T.*
            FROM CHANGETABLE(CHANGES [{table.Schema}].[{table.TableName}], @lastVersion) AS CT
            LEFT OUTER JOIN [{table.Schema}].[{table.TableName}] AS T ON {joinPredicates}
            {versionFilter}
            ORDER BY CT.SYS_CHANGE_VERSION ASC;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var pLastVersion = cmd.CreateParameter();
        pLastVersion.ParameterName = "@lastVersion";
        pLastVersion.Value = lastVersion;
        cmd.Parameters.Add(pLastVersion);

        if (exactVersion is null)
        {
            var pBatchSize = cmd.CreateParameter();
            pBatchSize.ParameterName = "@batchSize";
            pBatchSize.Value = batchSize;
            cmd.Parameters.Add(pBatchSize);
        }
        else
        {
            var pVersion = cmd.CreateParameter();
            pVersion.ParameterName = "@version";
            pVersion.Value = exactVersion.Value;
            cmd.Parameters.Add(pVersion);
        }

        var changes = new List<(long Version, CdcEvent Event)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var changeVersion = reader.GetInt64(reader.GetOrdinal("SYS_CHANGE_VERSION"));
            var changeOpChar = reader.GetString(reader.GetOrdinal("SYS_CHANGE_OPERATION")).Trim().ToUpperInvariant();

            var operation = changeOpChar switch
            {
                "I" => CdcOperation.Insert,
                "U" => CdcOperation.Update,
                "D" => CdcOperation.Delete,
                _ => CdcOperation.Update
            };

            var rowData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            string? tenantId = null;
            var pkValues = new List<string>();

            for (var i = 0; i < reader.FieldCount; i++)
            {
                var colName = reader.GetName(i);
                var val = reader.IsDBNull(i) ? null : reader.GetValue(i);

                if (colName.StartsWith("PK_", StringComparison.OrdinalIgnoreCase))
                {
                    var purePk = colName[3..];
                    pkValues.Add(val?.ToString() ?? string.Empty);

                    if (operation == CdcOperation.Delete && !rowData.ContainsKey(purePk))
                    {
                        rowData[purePk] = val;
                    }

                    continue;
                }

                if (colName.Equals("SYS_CHANGE_VERSION", StringComparison.OrdinalIgnoreCase) ||
                    colName.Equals("SYS_CHANGE_OPERATION", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (colName.Equals("TenantId", StringComparison.OrdinalIgnoreCase) ||
                    colName.Equals("tenant_id", StringComparison.OrdinalIgnoreCase))
                {
                    tenantId = val?.ToString();
                }

                rowData[colName] = val;
            }

            // Deterministic id: a re-published batch after a crash carries the same ids, so consumers can deduplicate.
            var eventId = $"{table.Domain}_{table.TableName}_{changeVersion}_{string.Join('|', pkValues)}";
            changes.Add((changeVersion, new CdcEvent(
                EventId: eventId,
                Table: table,
                Operation: operation,
                TenantId: tenantId,
                Before: null,
                After: rowData,
                Timestamp: DateTimeOffset.UtcNow,
                Metadata: new Dictionary<string, string>
                {
                    ["Source"] = "MssqlChangeTracking",
                    ["SysChangeVersion"] = changeVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })));
        }

        return changes;
    }

    private async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(DbConnection conn, TableIdentifier table, CancellationToken ct)
    {
        if (_pkCache.TryGetValue(table, out var cached))
        {
            return cached;
        }

        var sql = """
            SELECT c.name
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(@fullName)
            ORDER BY ic.key_ordinal;
            """;

        List<string> list = [];
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var pName = cmd.CreateParameter();
            pName.ParameterName = "@fullName";
            pName.Value = $"[{table.Schema}].[{table.TableName}]";
            cmd.Parameters.Add(pName);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(reader.GetString(0));
            }
        }

        // Transient query failures propagate and take the normal retry path; only a genuinely missing key is a configuration error.
        if (list.Count == 0)
        {
            throw new CdcSourceConfigurationException($"Tracked table '{table}' has no primary key; change tracking requires one.");
        }

        _pkCache[table] = list;
        return list;
    }

    private async Task<long?> GetMinValidVersionAsync(DbConnection conn, TableIdentifier table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@fullName));";
        var pName = cmd.CreateParameter();
        pName.ParameterName = "@fullName";
        pName.Value = $"[{table.Schema}].[{table.TableName}]";
        cmd.Parameters.Add(pName);

        var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return res != null && res != DBNull.Value && long.TryParse(res.ToString(), out var minVer) ? minVer : null;
    }
}
