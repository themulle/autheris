namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
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
                string.Equals(s.Provider, "sqlserver", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Provider, "mssql", StringComparison.OrdinalIgnoreCase))?.ConnectionString ?? string.Empty;

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
            throw new ArgumentException($"Sicherheitsfehler: Ungültiger SQL-Identifier '{identifier}'. Nur alphanumerische Zeichen und Unterstriche sind zulässig.", paramName);
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

    public async Task<int> PollTableChangesAsync(TableIdentifier table, CancellationToken ct = default)
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

        var lastVersion = await _watermarkStore.GetWatermarkAsync(table, ct).ConfigureAwait(false);

        try
        {
            await using var conn = await _connectionFactory.CreateOpenConnectionAsync(dsOptions, ct).ConfigureAwait(false);

            // 1. Resolve Primary Key Columns
            var pkColumns = await GetPrimaryKeyColumnsAsync(conn, table, ct).ConfigureAwait(false);
            if (pkColumns.Count == 0)
            {
                _logger.LogWarning("No primary key found for tracked table {Table}. Defaulting to [Id].", table);
                pkColumns = ["Id"];
            }

            foreach (var pk in pkColumns)
            {
                ValidateSqlIdentifier(pk, nameof(pk));
            }

            // 2. Validate Retention / Minimum Valid Version
            var minValidVersion = await GetMinValidVersionAsync(conn, table, ct).ConfigureAwait(false);
            if (minValidVersion.HasValue && lastVersion < minValidVersion.Value)
            {
                _logger.LogWarning(
                    "MSSQL Change Tracking watermark {LastVersion} for table {Table} is older than min valid version {MinValidVersion}. Resetting watermark.",
                    lastVersion, table, minValidVersion.Value);
                lastVersion = Math.Max(0, minValidVersion.Value - 1);
            }

            // 3. Query CHANGETABLE changes with sanitized bracket escaping
            var joinPredicates = string.Join(" AND ", pkColumns.Select(pk => $"T.[{pk}] = CT.[{pk}]"));
            var sql = $"""
                SELECT TOP (@batchSize)
                    CT.SYS_CHANGE_VERSION,
                    CT.SYS_CHANGE_OPERATION,
                    {string.Join(", ", pkColumns.Select(pk => $"CT.[{pk}] AS [PK_{pk}]"))},
                    T.*
                FROM CHANGETABLE(CHANGES [{table.Schema}].[{table.TableName}], @lastVersion) AS CT
                LEFT OUTER JOIN [{table.Schema}].[{table.TableName}] AS T ON {joinPredicates}
                ORDER BY CT.SYS_CHANGE_VERSION ASC;
                """;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var pLastVersion = cmd.CreateParameter();
            pLastVersion.ParameterName = "@lastVersion";
            pLastVersion.Value = lastVersion;
            cmd.Parameters.Add(pLastVersion);

            var pBatchSize = cmd.CreateParameter();
            pBatchSize.ParameterName = "@batchSize";
            pBatchSize.Value = batchSize;
            cmd.Parameters.Add(pBatchSize);

            var eventsProcessed = 0;
            var maxVersion = lastVersion;

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

                // Read row dictionary
                var rowData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                string? tenantId = null;
                string pkValueStr = string.Empty;

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var colName = reader.GetName(i);
                    var val = reader.IsDBNull(i) ? null : reader.GetValue(i);

                    if (colName.StartsWith("PK_", StringComparison.OrdinalIgnoreCase))
                    {
                        var purePk = colName[3..];
                        if (string.IsNullOrEmpty(pkValueStr) && val != null)
                        {
                            pkValueStr = val.ToString() ?? "";
                        }
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

                var eventId = $"{table.Domain}_{table.TableName}_{changeVersion}_{pkValueStr}";
                var cdcEvent = new CdcEvent(
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
                        ["SysChangeVersion"] = changeVersion.ToString()
                    }
                );

                await _eventChannel.PublishAsync(cdcEvent, ct).ConfigureAwait(false);
                eventsProcessed++;
                maxVersion = Math.Max(maxVersion, changeVersion);
            }

            if (maxVersion > lastVersion)
            {
                await _watermarkStore.SetWatermarkAsync(table, maxVersion, ct).ConfigureAwait(false);
            }

            return eventsProcessed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while polling CHANGETABLE for {Table} at version {Version}.", table, lastVersion);
            return 0;
        }
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

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var pName = cmd.CreateParameter();
            pName.ParameterName = "@fullName";
            pName.Value = $"[{table.Schema}].[{table.TableName}]";
            cmd.Parameters.Add(pName);

            var list = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(reader.GetString(0));
            }

            if (list.Count > 0)
            {
                _pkCache[table] = list;
                return list;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query sys.indexes for PK on {Table}. Falling back to default [Id].", table);
        }

        return ["Id"];
    }

    private async Task<long?> GetMinValidVersionAsync(DbConnection conn, TableIdentifier table, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@fullName));";
            var pName = cmd.CreateParameter();
            pName.ParameterName = "@fullName";
            pName.Value = $"[{table.Schema}].[{table.TableName}]";
            cmd.Parameters.Add(pName);

            var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (res != null && res != DBNull.Value && long.TryParse(res.ToString(), out var minVer))
            {
                return minVer;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CHANGE_TRACKING_MIN_VALID_VERSION query failed for {Table}.", table);
        }

        return null;
    }
}
