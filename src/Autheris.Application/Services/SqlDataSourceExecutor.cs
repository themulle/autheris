using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Services;

public sealed class SqlDataSourceExecutor : IDataSourceExecutor
{
    private readonly ISqlConnectionFactory? _connectionFactory;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly ILogger<SqlDataSourceExecutor>? _logger;
    private readonly Microsoft.Extensions.Hosting.IHostEnvironment? _environment;
    private readonly IColumnMaskingProvider? _maskingProvider;

    public DataSourceType SupportedType => DataSourceType.Sql;
    public const int MaxAllowedBinaryBytes = 16 * 1024 * 1024; // 16 MB limit per binary column value (SEC-SPEC-05)

    public SqlDataSourceExecutor(
        ISqlConnectionFactory? connectionFactory = null,
        IOptions<GatewayOptions>? options = null,
        ILogger<SqlDataSourceExecutor>? logger = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        IColumnMaskingProvider? maskingProvider = null)
    {
        _connectionFactory = connectionFactory;
        _options = options;
        _logger = logger;
        _environment = environment;
        _maskingProvider = maskingProvider;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // SEC-AC-01: Zero-Trust / Fail-Closed: Refuse execution if table access is denied by policy
        if (!context.AccessDecision.IsAllowed)
        {
            var reasons = context.AccessDecision.DeniedReasons.Count > 0
                ? string.Join("; ", context.AccessDecision.DeniedReasons)
                : "Tabelle ist durch Zugriffsrichtlinie gesperrt.";
            throw new SecurityException($"Zero-Trust-Verletzung: Zugriff auf Tabelle '{context.Metadata.Identifier}' verweigert: {reasons}");
        }

        // SEC-AC-02: Zero-Trust: Validate RLS filter early before any connection or query execution
        if (!string.IsNullOrWhiteSpace(context.AccessDecision.CombinedRowFilterSql))
        {
            Autheris.Application.Sql.SqlSecurityValidator.ValidatePredicateSql(
                context.AccessDecision.CombinedRowFilterSql,
                "CombinedRowFilterSql");
        }

        // SEC-01: Side-channel inference protection: verify column filters target only Clear columns
        foreach (var (argKey, argVal) in context.Arguments)
        {
            if (string.Equals(argKey, "limit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argKey, "offset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var matchingCol = context.Metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, argKey, StringComparison.OrdinalIgnoreCase));
            if (matchingCol != null && argVal != null)
            {
                // SEC H-10: Filter only on effectively Clear columns (catalog-sensitive/masked columns need an explicit Clear).
                var access = context.AccessDecision.GetEffectiveColumnAccess(matchingCol.ColumnName, context.Metadata);
                if (access != ColumnAccessLevel.Clear)
                {
                    throw new SecurityException($"Zero-Trust-Verletzung: Filtern auf Spalte '{matchingCol.ColumnName}' in Tabelle '{context.Metadata.Identifier}' ist nicht gestattet (Zugriffsebene: {access}).");
                }
            }
        }

        // Check if a real SQL connection is configured for this data source
        var configuredConnections = _options?.Value?.DataSources?.Connections;
        DataSourceConnectionOptions? connOptions = null;

        if (configuredConnections != null && !string.IsNullOrWhiteSpace(context.SourceName))
        {
            configuredConnections.TryGetValue(context.SourceName, out connOptions);
        }

        // If no real connection is configured, or connection factory is missing, execute synthetic demo data generator (fallback for dev & unit tests)
        if (connOptions == null || string.IsNullOrWhiteSpace(connOptions.ConnectionString) || _connectionFactory == null)
        {
            bool isDevOrTest = _environment == null ||
                               string.Equals(_environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
            bool isExplicitlyAllowed = _options?.Value?.AreExternalSystemsMockedIfUnreachable == true;

            if (!isDevOrTest && !isExplicitlyAllowed)
            {
                throw new InvalidOperationException($"Die SQL-Datenquelle '{context.SourceName}' besitzt keine gültige Datenbankverbindung. Synthetischer Daten-Fallback ist in Produktivumgebungen deaktiviert.");
            }

            return GenerateSyntheticRows(context);
        }

        return await ExecuteRealSqlQueryAsync(context, connOptions, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteRealSqlQueryAsync(
        DataSourceExecutionContext context,
        DataSourceConnectionOptions connOptions,
        CancellationToken ct)
    {
        var metadata = context.Metadata;
        var dialect = metadata.Dialect;
        context.Items["RlsPushdownExecuted"] = true;

        ArgumentNullException.ThrowIfNull(_connectionFactory);
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(1, connOptions.CommandTimeoutSeconds);

        // 1. Column Projections (Zero-Trust: Only authorized requested fields + dialect-specific special type mapping)
        var columnsToSelect = (context.RequestedFields != null && context.RequestedFields.Count > 0)
            ? context.RequestedFields
            : metadata.Columns.Select(c => c.ColumnName).ToList();

        // SEC-AC-02: Zero-Trust: Exclude any columns marked with ColumnAccessLevel.Deny
        var authorizedColumns = columnsToSelect
            .Where(col => context.AccessDecision.GetEffectiveColumnAccess(col, metadata) != ColumnAccessLevel.Deny)
            .ToList();

        var selectParts = new List<string>(authorizedColumns.Count);
        bool hasMaskedCols = false;

        // SEC H-13: HMAC pseudonymization is computed in the gateway after reading (keyed with the resolved secret),
        // never in SQL with a secret (or secret name) embedded in the statement text.
        var tenantVal = context.Tenant?.Value ?? context.Principal.FindFirst("tenant")?.Value ?? TenantId.LegacySingleTenant.Value;
        var gatewayHmacColumns = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in authorizedColumns)
        {
            var colDef = metadata.GetColumn(col);

            // SEC H-10: Same effective access decision as used for filter validation (catalog-sensitive -> Mask unless explicit Clear)
            var access = context.AccessDecision.GetEffectiveColumnAccess(col, metadata);

            if (access == ColumnAccessLevel.Mask && _options?.Value?.IsColumnMaskingDisabled != true)
            {
                hasMaskedCols = true;
                if (_maskingProvider != null &&
                    metadata.ColumnMaskingRules.TryGetValue(col, out var hmacRule) &&
                    IsHmacRule(hmacRule))
                {
                    gatewayHmacColumns[col] = CreateTenantScopedHmacRule(hmacRule, tenantVal, _options?.Value?.DataMasking?.HmacKeyId);
                    selectParts.Add(BuildColumnProjection(col, colDef?.DataType, dialect));
                }
                else
                {
                    selectParts.Add(BuildMaskedColumnProjection(col, colDef?.DataType, dialect, metadata));
                }
            }
            else
            {
                selectParts.Add(BuildColumnProjection(col, colDef?.DataType, dialect));
            }
        }

        if (hasMaskedCols)
        {
            context.Items["InDbColumnMaskingExecuted"] = true;
        }

        if (selectParts.Count == 0)
        {
            selectParts.Add("1 AS __unauthorized_placeholder");
        }

        var selectClause = string.Join(", ", selectParts);
        var fromTable = dialect.FormatTableIdentifier(metadata.Identifier);

        // 2. WHERE Clause: Push down RLS predicate + Tenant isolation + any applicable equality arguments
        var whereParts = new List<string>();
        var paramIndex = 0;

        // Stufe 1: Applikationsseitiger erzwungener Tenant-Filter (Defense in Depth)
        // Review E-5: the tenant column is found under any of its usual spellings (tenant_id, TenantId, tenantId, ...).
        var tenantColumn = TableMetadata.RequireTenantColumnOrThrow(
            metadata,
            _options?.Value?.DataSources?.RequireTenantColumn == true,
            _options?.Value?.DataSources?.TenantColumnExemptTables);
        if (tenantColumn != null)
        {
            var pTenant = $"@p_tenant_{paramIndex++}";
            whereParts.Add($"{dialect.QuoteIdentifier(tenantColumn)} = {pTenant}");
            var tp = command.CreateParameter();
            tp.ParameterName = pTenant;
            tp.Value = tenantVal;
            command.Parameters.Add(tp);
        }

        if (!string.IsNullOrWhiteSpace(context.AccessDecision.CombinedRowFilterSql))
        {
            Autheris.Application.Sql.SqlSecurityValidator.ValidatePredicateSql(
                context.AccessDecision.CombinedRowFilterSql,
                "CombinedRowFilterSql");
            whereParts.Add($"({context.AccessDecision.CombinedRowFilterSql})");

            if (context.AccessDecision.RowFilterParameters != null)
            {
                foreach (var (pName, pVal) in context.AccessDecision.RowFilterParameters)
                {
                    var p = command.CreateParameter();
                    p.ParameterName = pName;
                    p.Value = pVal ?? DBNull.Value;
                    command.Parameters.Add(p);
                }
            }
        }

        foreach (var (argKey, argVal) in context.Arguments)
        {
            if (string.Equals(argKey, "limit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argKey, "offset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var matchingCol = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, argKey, StringComparison.OrdinalIgnoreCase));
            if (matchingCol != null && argVal != null)
            {
                // SEC-01 / SEC H-10: Filtering on columns that are not effectively Clear (Mask/Deny or catalog-sensitive without explicit Clear) is forbidden to prevent side-channel inference
                var access = context.AccessDecision.GetEffectiveColumnAccess(matchingCol.ColumnName, metadata);
                if (access != ColumnAccessLevel.Clear)
                {
                    throw new SecurityException($"Zero-Trust-Verletzung: Filtern auf Spalte '{matchingCol.ColumnName}' in Tabelle '{metadata.Identifier}' ist nicht gestattet (Zugriffsebene: {access}).");
                }

                var paramName = $"@p{paramIndex++}";
                whereParts.Add($"{dialect.QuoteIdentifier(matchingCol.ColumnName)} = {paramName}");

                var p = command.CreateParameter();
                p.ParameterName = paramName;
                p.Value = argVal;
                command.Parameters.Add(p);
            }
        }

        var sqlBuilder = new StringBuilder();
        // The reserved alias lets correlated row filters (EXISTS ... = autheris_target.fk) bind to this table.
        var aliasKeyword = dialect == DatabaseDialect.Oracle ? " " : " AS ";
        sqlBuilder.Append($"SELECT {selectClause} FROM {fromTable}{aliasKeyword}{dialect.QuoteIdentifier(TrinoSqlEngine.RowFilterAliases.Target)}");

        if (whereParts.Count > 0)
        {
            sqlBuilder.Append(" WHERE ");
            sqlBuilder.Append(string.Join(" AND ", whereParts));
        }

        // 3. Pagination Pushdown (Dialect-specific)
        var limit = Math.Max(1, context.Limit);
        var offset = Math.Max(0, context.Offset);

        var limitParam = command.CreateParameter();
        limitParam.ParameterName = "@gql_limit";
        limitParam.Value = limit;
        command.Parameters.Add(limitParam);

        var offsetParam = command.CreateParameter();
        offsetParam.ParameterName = "@gql_offset";
        offsetParam.Value = offset;
        command.Parameters.Add(offsetParam);

        switch (dialect)
        {
            case DatabaseDialect.SqlServer:
                // SQL Server requires an ORDER BY clause for OFFSET-FETCH.
                var orderCol = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, "id", StringComparison.OrdinalIgnoreCase))?.ColumnName
                               ?? (metadata.Columns.Count > 0 ? metadata.Columns[0].ColumnName : null);
                var orderClause = orderCol != null ? dialect.QuoteIdentifier(orderCol) : "(SELECT 1)";
                sqlBuilder.Append($" ORDER BY {orderClause} OFFSET @gql_offset ROWS FETCH NEXT @gql_limit ROWS ONLY");
                break;

            case DatabaseDialect.Oracle:
                sqlBuilder.Append(" OFFSET @gql_offset ROWS FETCH NEXT @gql_limit ROWS ONLY");
                break;

            case DatabaseDialect.Sqlite:
            case DatabaseDialect.PostgreSql:
            case DatabaseDialect.Databricks:
            default:
                sqlBuilder.Append(" LIMIT @gql_limit OFFSET @gql_offset");
                break;
        }

        command.CommandText = sqlBuilder.ToString();
        _logger?.LogDebug("Executing SQL Backend query for table '{Table}' with {ParamCount} parameters", context.Metadata.Identifier.ToQualifiedName(), command.Parameters.Count);

        // Stufe 2: Native PostgreSQL Transaktions-Scoped Session RLS (SET LOCAL app.tenant_id = @p)
        DbTransaction? tx = null;
        try
        {
            // The configured connection provider decides, with the same rule as SqlConnectionFactory (no provider
            // opens SQLite). The catalog dialect may differ (it defaults to PostgreSQL); set_config exists on PostgreSQL only.
            var provider = connOptions.Provider?.Trim().ToLowerInvariant() ?? "sqlite";
            if (provider is "postgres" or "postgresql" or "npgsql")
            {
                tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                command.Transaction = tx;

                await using var setCmd = connection.CreateCommand();
                setCmd.Transaction = tx;
                setCmd.CommandText = "SELECT set_config('app.tenant_id', @p_tenant, true), set_config('TimeZone', 'UTC', true);";
                var pTenant = setCmd.CreateParameter();
                pTenant.ParameterName = "@p_tenant";
                pTenant.Value = tenantVal;
                setCmd.Parameters.Add(pTenant);
                await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var results = new List<IReadOnlyDictionary<string, object?>>(Math.Min(Math.Max(context.Limit, 16), 1024));

            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, ct).ConfigureAwait(false))
            {
                int fieldCount = reader.FieldCount;
                var columnNames = new string[fieldCount];
                for (int i = 0; i < fieldCount; i++)
                {
                    columnNames[i] = reader.GetName(i);
                }

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(fieldCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < fieldCount; i++)
                    {
                        var rawVal = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        var normalized = NormalizeReadValue(rawVal, columnNames[i]);
                        if (_maskingProvider != null && gatewayHmacColumns.TryGetValue(columnNames[i], out var hmacRule))
                        {
                            normalized = _maskingProvider.MaskValue(columnNames[i], normalized, hmacRule);
                        }
                        row[columnNames[i]] = normalized;
                    }
                    results.Add(row);
                }
            }

            if (tx != null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            return results;
        }
        catch
        {
            if (tx != null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (tx != null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static string BuildMaskedColumnProjection(string columnName, string? dataType, DatabaseDialect dialect, TableMetadata tableMeta)
    {
        ArgumentNullException.ThrowIfNull(tableMeta);
        DatabaseDialectExtensions.ValidateIdentifier(columnName);
        var quotedCol = dialect.QuoteIdentifier(columnName);
        string maskExpr;

        if (tableMeta.ColumnMaskingRules.TryGetValue(columnName, out var rule))
        {
            var ruleType = rule.RuleType?.ToUpperInvariant() ?? "REDACT";
            if (ruleType == "NULLIFY")
            {
                maskExpr = "NULL";
            }
            else if (IsHmacRule(rule))
            {
                // SEC H-13: No unkeyed in-DB hash. HMAC columns are pseudonymized in the gateway (IColumnMaskingProvider);
                // without a masking provider the column is redacted (fail-closed).
                maskExpr = "'***'";
            }
            else if (!string.IsNullOrWhiteSpace(rule.Replacement))
            {
                maskExpr = $"'{rule.Replacement.Replace("'", "''")}'";
            }
            else
            {
                maskExpr = "'***'";
            }
        }
        else
        {
            maskExpr = "'***'";
        }

        return $"{maskExpr} AS {quotedCol}";
    }

    private static bool IsHmacRule(MaskingRule rule) =>
        rule.RuleType?.ToUpperInvariant() is "HMAC" or "HMAC_SHA256" or "HASH";

    /// <summary>
    /// SEC H-13: Derives a tenant-scoped HMAC key id so pseudonyms cannot be correlated across tenants.
    /// The actual key derivation (HMAC over the master secret) happens inside <see cref="IColumnMaskingProvider"/>.
    /// </summary>
    private static MaskingRule CreateTenantScopedHmacRule(MaskingRule rule, string tenant, string? defaultKeyId)
    {
        var baseKeyId = !string.IsNullOrWhiteSpace(rule.HmacKeyId) ? rule.HmacKeyId : (defaultKeyId ?? "default");
        return new MaskingRule
        {
            Id = rule.Id,
            TableColumnId = rule.TableColumnId,
            RuleType = "HMAC_SHA256",
            PatternOrFormat = rule.PatternOrFormat,
            Replacement = rule.Replacement,
            HmacKeyId = $"{baseKeyId}|tenant:{tenant}"
        };
    }

    public static string BuildColumnProjection(string columnName, string? dataType, DatabaseDialect dialect)
    {
        DatabaseDialectExtensions.ValidateIdentifier(columnName);
        var quotedCol = dialect.QuoteIdentifier(columnName);

        if (string.IsNullOrWhiteSpace(dataType))
        {
            return quotedCol;
        }

        var normalizedType = dataType.Trim().ToLowerInvariant();

        // Special case MSSQL: "timestamp" is a deprecated synonym for "rowversion" (8-byte binary token, NOT datetime!)
        if (dialect == DatabaseDialect.SqlServer && normalizedType is "timestamp" or "rowversion")
        {
            return quotedCol; // Handled as binary in reader -> Base64
        }

        // 1. Geospatial Types (geometry, geography, spatial, point, polygon, linestring, multipolygon, multipoint, sdo_geometry)
        if (normalizedType is "geometry" or "geography" or "spatial" or "point" or "polygon" or "linestring" or "multipolygon" or "multipoint" or "sdo_geometry")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"ST_AsGeoJSON({quotedCol}) AS {quotedCol}",
                DatabaseDialect.SqlServer => $"({quotedCol}.STAsText()) AS {quotedCol}",
                DatabaseDialect.Sqlite => $"AsGeoJSON({quotedCol}) AS {quotedCol}",
                DatabaseDialect.Oracle => $"SDO_UTIL.TO_GEOJSON({quotedCol}) AS {quotedCol}",
                _ => quotedCol
            };
        }

        // 2. Binary Types (bytea, binary, varbinary, blob, image, raw, long raw)
        if (normalizedType is "bytea" or "binary" or "varbinary" or "blob" or "image" or "raw" or "long raw")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"encode({quotedCol}, 'base64') AS {quotedCol}",
                DatabaseDialect.Sqlite => $"hex({quotedCol}) AS {quotedCol}",
                DatabaseDialect.Databricks => $"base64({quotedCol}) AS {quotedCol}",
                DatabaseDialect.Oracle => $"RAWTOHEX({quotedCol}) AS {quotedCol}",
                _ => quotedCol
            };
        }

        // 3. High-precision / timezone timestamps (timestamp, timestamptz, datetime2, datetimeoffset, etc.)
        if (normalizedType is "timestamptz" or "datetimeoffset" or "datetime2" or "datetime" or "smalldatetime" or "timestamp" or "timestamp_ntz" or "timestamp with time zone" or "timestamp with local time zone")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"to_char({quotedCol}, 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') AS {quotedCol}",
                DatabaseDialect.SqlServer => $"CONVERT(VARCHAR(33), {quotedCol}, 126) AS {quotedCol}",
                DatabaseDialect.Sqlite => $"strftime('%Y-%m-%dT%H:%M:%fZ', {quotedCol}) AS {quotedCol}",
                DatabaseDialect.Databricks => $"date_format({quotedCol}, 'yyyy-MM-dd''T''HH:mm:ss.SSS''Z''') AS {quotedCol}",
                DatabaseDialect.Oracle => $"TO_CHAR({quotedCol}, 'YYYY-MM-DD\"T\"HH24:MI:SS.FF6\"Z\"') AS {quotedCol}",
                _ => quotedCol
            };
        }

        // 4. Date-only (date)
        if (normalizedType is "date")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"to_char({quotedCol}, 'YYYY-MM-DD') AS {quotedCol}",
                DatabaseDialect.SqlServer => $"CONVERT(VARCHAR(10), {quotedCol}, 23) AS {quotedCol}",
                DatabaseDialect.Oracle => $"TO_CHAR({quotedCol}, 'YYYY-MM-DD') AS {quotedCol}",
                DatabaseDialect.Sqlite => $"strftime('%Y-%m-%d', {quotedCol}) AS {quotedCol}",
                DatabaseDialect.Databricks => $"date_format({quotedCol}, 'yyyy-MM-dd') AS {quotedCol}",
                _ => quotedCol
            };
        }

        // 5. Time-only (time, timetz, time without time zone)
        if (normalizedType is "time" or "timetz" or "time without time zone")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"to_char({quotedCol}, 'HH24:MI:SS.US') AS {quotedCol}",
                DatabaseDialect.SqlServer => $"CONVERT(VARCHAR(16), {quotedCol}, 114) AS {quotedCol}",
                DatabaseDialect.Sqlite => $"strftime('%H:%M:%f', {quotedCol}) AS {quotedCol}",
                _ => quotedCol
            };
        }

        return quotedCol;
    }

    public static object? NormalizeReadValue(object? rawValue, string? columnName = null)
    {
        if (rawValue == null || rawValue is DBNull)
        {
            return null;
        }

        // SEC-SPEC-05: LOH allocation defense & safe base64 representation for binary blobs
        if (rawValue is byte[] bytes)
        {
            if (bytes.Length > MaxAllowedBinaryBytes)
            {
                throw new SecurityException($"Die Binärspalte '{columnName ?? "unbekannt"}' überschreitet die zulässige Maximalgröße von {MaxAllowedBinaryBytes / (1024 * 1024)} MB.");
            }
            return Convert.ToBase64String(bytes);
        }

        // SEC-SPEC-04: Deterministic culture-invariant UTC normalization
        if (rawValue is DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Unspecified)
            {
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
            return dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        if (rawValue is DateTimeOffset dto)
        {
            return dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        if (rawValue is DateOnly d)
        {
            return d.ToString("O", CultureInfo.InvariantCulture);
        }

        if (rawValue is TimeOnly t)
        {
            return t.ToString("O", CultureInfo.InvariantCulture);
        }

        if (rawValue is TimeSpan ts)
        {
            return ts.ToString("c", CultureInfo.InvariantCulture);
        }

        // Defense in depth: normalize ISO strings from DB without explicit UTC indicator to canonical UTC 'Z'
        if (rawValue is string s && s.Length >= 19 && s[10] == 'T' && !s.EndsWith('Z') && !s.Contains('+') && s.IndexOf('-', 11) == -1)
        {
            return s + "Z";
        }

        // Defense in depth: Spatial CLR objects that were not projected via SQL
        var typeName = rawValue.GetType().FullName;
        if (typeName != null && (typeName.Contains("Spatial", StringComparison.OrdinalIgnoreCase) ||
                                 typeName.Contains("Geometry", StringComparison.OrdinalIgnoreCase) ||
                                 typeName.Contains("Geography", StringComparison.OrdinalIgnoreCase)))
        {
            return rawValue.ToString();
        }

        return rawValue;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> GenerateSyntheticRows(DataSourceExecutionContext context)
    {
        context.Items["IsSyntheticMock"] = true;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var metadata = context.Metadata;
        var count = Math.Max(1, context.Limit);
        var offset = Math.Max(0, context.Offset);

        for (int i = 1; i <= count; i++)
        {
            var rowNum = offset + i;
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var col in metadata.Columns)
            {
                // SEC-AC-02: Zero-Trust: Do not emit synthetic data for Denied columns
                if (context.AccessDecision.GetColumnAccess(col.ColumnName) == ColumnAccessLevel.Deny)
                {
                    continue;
                }

                object? rawVal = col.ColumnName.ToLowerInvariant() switch
                {
                    "id" => rowNum,
                    "name" => $"Sample {metadata.Identifier.TableName} Record #{rowNum}",
                    "amount" => 100.50m * rowNum,
                    "email" => $"user{rowNum}@corp.local",
                    "created_at" => DateTimeOffset.UtcNow.AddDays(-rowNum),
                    _ => GenerateSyntheticValueForType(col.DataType, rowNum)
                };

                dict[col.ColumnName] = rawVal;
            }

            rows.Add(dict);
        }

        return rows;
    }

    private static object? GenerateSyntheticValueForType(string? dataType, int rowNum)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return $"Value_{rowNum}";
        }

        var normalizedType = dataType.Trim().ToLowerInvariant();
        if (normalizedType is "geometry" or "geography" or "spatial" or "point" or "polygon" or "linestring")
        {
            return $$"""{"type":"Point","coordinates":[13.4{{rowNum}},52.5{{rowNum}}]}""";
        }

        if (normalizedType is "bytea" or "binary" or "varbinary" or "blob" or "image")
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes($"binary_payload_{rowNum}"));
        }

        if (normalizedType is "timestamptz" or "datetimeoffset" or "datetime2" or "timestamp")
        {
            return DateTimeOffset.UtcNow.AddDays(-rowNum).ToString("O", CultureInfo.InvariantCulture);
        }

        return $"Value_{rowNum}";
    }
}
