using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
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
    private readonly IDbSessionContextInitializer _sessionInitializer;

    public DataSourceType SupportedType => DataSourceType.Sql;
    public const int MaxAllowedBinaryBytes = 16 * 1024 * 1024; // 16 MB limit per binary column value (SEC-SPEC-05)

    public SqlDataSourceExecutor(
        ISqlConnectionFactory? connectionFactory = null,
        IOptions<GatewayOptions>? options = null,
        ILogger<SqlDataSourceExecutor>? logger = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        IColumnMaskingProvider? maskingProvider = null,
        IDbSessionContextInitializer? sessionInitializer = null)
    {
        _connectionFactory = connectionFactory;
        _options = options;
        _logger = logger;
        _environment = environment;
        _maskingProvider = maskingProvider;
        _sessionInitializer = sessionInitializer ?? new DbSessionContextInitializer();
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
                : "Table is blocked by access policy.";
            throw new SecurityException($"Zero-Trust violation: Access to table '{context.Metadata.Identifier}' denied: {reasons}");
        }

        // SEC-AC-02: Zero-Trust: Validate RLS filter early before any connection or query execution
        if (!string.IsNullOrWhiteSpace(context.AccessDecision.CombinedRowFilterSql))
        {
            Autheris.Application.Sql.SqlSecurityValidator.ValidateRowFilter(context.AccessDecision);
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
                    throw new SecurityException($"Zero-Trust violation: Filtering on column '{matchingCol.ColumnName}' in table '{context.Metadata.Identifier}' is not permitted (access level: {access}).");
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
            bool isDev = _environment != null &&
                         string.Equals(_environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
            bool isExplicitlyAllowed = _options?.Value?.AreExternalSystemsMockedIfUnreachable == true;

            if (!isDev && !isExplicitlyAllowed)
            {
                throw new GatewayNotImplementedException($"The SQL data source '{context.SourceName}' has no active database connection. Synthetic data fallback is disabled outside the Development environment.");
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

        // D-1: Fail-closed dialect alignment between catalog and connection provider (same provider mapping as
        // SqlConnectionFactory, Architecture 5).
        if (!DataSourceProvider.TryResolveDialect(connOptions.Provider, out var providerDialect) || dialect != providerDialect)
        {
            throw new InvalidOperationException($"Catalog dialect '{dialect}' does not match the provider '{connOptions.Provider}' of data source '{context.SourceName}' for table '{metadata.Identifier.ToQualifiedName()}'.");
        }

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
        var tenantVal = context.Tenant?.Value
            ?? context.Principal?.FindFirst("tenant_id")?.Value
            ?? context.Principal?.FindFirst("tenant")?.Value
            ?? TenantId.LegacySingleTenant.Value;
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
            Autheris.Application.Sql.SqlSecurityValidator.ValidateRowFilter(context.AccessDecision);
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
                    throw new SecurityException($"Zero-Trust violation: Filtering on column '{matchingCol.ColumnName}' in table '{metadata.Identifier}' is not permitted (access level: {access}).");
                }

                var paramName = $"@p{paramIndex++}";
                whereParts.Add($"{dialect.QuoteIdentifier(matchingCol.ColumnName)} = {paramName}");

                var p = command.CreateParameter();
                p.ParameterName = paramName;
                p.Value = argVal;
                command.Parameters.Add(p);
            }
        }

        // Befund 2.1: $filter pushdown with Zero-Trust enforcement and parameterization
        if (context.Items.TryGetValue(TableQueryItems.Filter, out var filterObj) && filterObj is TableFilterClause filterClause)
        {
            foreach (var colName in filterClause.ReferencedColumns)
            {
                var matchingCol = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, colName, StringComparison.OrdinalIgnoreCase));
                if (matchingCol != null)
                {
                    var access = context.AccessDecision.GetEffectiveColumnAccess(matchingCol.ColumnName, metadata);
                    if (access != ColumnAccessLevel.Clear)
                    {
                        throw new SecurityException($"Zero-Trust violation: Filtering on column '{matchingCol.ColumnName}' in table '{metadata.Identifier}' is not permitted (access level: {access}).");
                    }
                }
            }

            var filterPredicate = filterClause.GetSqlPredicate(dialect);
            whereParts.Add($"({filterPredicate})");

            foreach (var (pName, pVal) in filterClause.Parameters)
            {
                var p = command.CreateParameter();
                p.ParameterName = pName;
                p.Value = pVal ?? DBNull.Value;
                command.Parameters.Add(p);
            }
        }

        var sqlBuilder = new StringBuilder();
        // The reserved alias lets correlated row filters (EXISTS ... = autheris_target.fk) bind to this table.
        var aliasKeyword = dialect == DatabaseDialect.Oracle ? " " : " AS ";
        var fromClause = $" FROM {fromTable}{aliasKeyword}{dialect.QuoteIdentifier(TrinoSqlEngine.RowFilterAliases.Target)}";
        var whereClause = whereParts.Count > 0 ? " WHERE " + string.Join(" AND ", whereParts) : string.Empty;
        sqlBuilder.Append($"SELECT {selectClause}{fromClause}{whereClause}");

        // 4a.3: ORDER BY of the request (validated against the catalog and the access decision by the gateway; checked
        // here again because the column name ends up in the statement text).
        string? requestedOrder = BuildOrderByClause(context, metadata, dialect);

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
                // If a primary key or 'id' column exists, prefer it to leverage clustered index order.
                // Otherwise fall back to (SELECT 1) to avoid an expensive full-table sort on an arbitrary first column.
                var pkCol = metadata.PrimaryKeyColumns.FirstOrDefault(pk => metadata.HasColumn(pk));
                var orderCol = pkCol ?? metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, "id", StringComparison.OrdinalIgnoreCase))?.ColumnName;
                var orderClause = requestedOrder ?? (orderCol != null ? dialect.QuoteIdentifier(orderCol) : "(SELECT 1)");
                sqlBuilder.Append($" ORDER BY {orderClause} OFFSET @gql_offset ROWS FETCH NEXT @gql_limit ROWS ONLY");
                break;

            case DatabaseDialect.Oracle:
                if (requestedOrder != null)
                {
                    sqlBuilder.Append($" ORDER BY {requestedOrder}");
                }
                sqlBuilder.Append(" OFFSET @gql_offset ROWS FETCH NEXT @gql_limit ROWS ONLY");
                break;

            case DatabaseDialect.Sqlite:
            case DatabaseDialect.PostgreSql:
            default:
                if (requestedOrder != null)
                {
                    sqlBuilder.Append($" ORDER BY {requestedOrder}");
                }
                sqlBuilder.Append(" LIMIT @gql_limit OFFSET @gql_offset");
                break;
        }

        command.CommandText = sqlBuilder.ToString();
        if (_options?.Value?.Logging?.LogGeneratedSql == true)
        {
            _logger?.LogDebug("SqlDataSourceExecutor: Generated SQL: {Sql}", command.CommandText);
        }
        _logger?.LogDebug("Executing SQL Backend query for table '{Table}' with {ParamCount} parameters", context.Metadata.Identifier.ToQualifiedName(), command.Parameters.Count);

        // Stufe 2: Native Session Context & Transaction RLS
        DbTransaction? tx = null;
        try
        {
            var userSid = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.PrimarySid)?.Value
                          ?? context.Principal?.FindFirst("sub")?.Value;

            if (!TenantId.TryParse(tenantVal, out var validatedTenantId))
            {
                _logger?.LogWarning("Invalid tenant identity claim rejected: {TenantVal}", tenantVal);
                throw new GatewayForbiddenException("Invalid tenant identity.");
            }

            tx = await _sessionInitializer.InitializeSessionAsync(
                connection,
                connOptions.Provider,
                validatedTenantId,
                userSid: userSid,
                purpose: null,
                requireTransaction: true,
                ct: ct).ConfigureAwait(false);

            if (tx != null)
            {
                command.Transaction = tx;
            }

            IReadOnlyList<IReadOnlyDictionary<string, object?>> results;
            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, ct).ConfigureAwait(false))
            {
                results = await ReadRowsAsync(reader, gatewayHmacColumns, context.Limit, ct).ConfigureAwait(false);
            }

            // 4a.3: total row count under exactly the same FROM/WHERE (tenant, row filter, arguments), same transaction.
            if (context.Items.TryGetValue(TableQueryItems.CountTotal, out var countRequested) && countRequested is true)
            {
                await using var countCommand = connection.CreateCommand();
                countCommand.CommandTimeout = command.CommandTimeout;
                countCommand.Transaction = tx;
                countCommand.CommandText = $"SELECT COUNT(*){fromClause}{whereClause}";
                foreach (DbParameter parameter in command.Parameters)
                {
                    if (parameter.ParameterName is "@gql_limit" or "@gql_offset")
                    {
                        continue;
                    }

                    var copy = countCommand.CreateParameter();
                    copy.ParameterName = parameter.ParameterName;
                    copy.Value = parameter.Value;
                    countCommand.Parameters.Add(copy);
                }

                var scalar = await countCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);
                context.Items[TableQueryItems.TotalCount] = Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (tx != null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            return results;
        }
        catch (Exception)
        {
            if (tx != null)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback transaction after error in SqlDataSourceExecutor");
                }
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

    /// <summary>
    /// 4a.3: ORDER BY clause of the request, or null. Only catalog columns with clear-text access are accepted.
    /// </summary>
    private static string? BuildOrderByClause(DataSourceExecutionContext context, TableMetadata metadata, DatabaseDialect dialect)
    {
        if (!context.Items.TryGetValue(TableQueryItems.OrderBy, out var orderObj) || orderObj is not IReadOnlyList<TableOrderBy> orderBy || orderBy.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(orderBy.Count);
        foreach (var item in orderBy)
        {
            var column = metadata.GetColumn(item.Column);
            if (column == null || context.AccessDecision.GetEffectiveColumnAccess(column.ColumnName, metadata) != ColumnAccessLevel.Clear)
            {
                throw new SecurityException($"Zero-Trust violation: Ordering by column '{item.Column}' in table '{metadata.Identifier}' is not permitted.");
            }

            parts.Add(dialect.QuoteIdentifier(column.ColumnName) + (item.Descending ? " DESC" : " ASC"));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Reads all rows of <paramref name="reader"/>, applying gateway-side HMAC pseudonymization.
    /// O11: stops as soon as the estimated response exceeds <c>GraphQL.MaxResponseBytes</c> (same limit and error code
    /// as the final check in GatewayExecutionService, but before the whole result sits in memory).
    /// O12: a value the driver cannot materialize ends in <see cref="GatewayUnsupportedColumnTypeException"/>.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsAsync(
        DbDataReader reader,
        IReadOnlyDictionary<string, MaskingRule> gatewayHmacColumns,
        int expectedRows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(gatewayHmacColumns);

        var maxBytes = _options?.Value?.GraphQL?.MaxResponseBytes > 0 ? _options.Value.GraphQL.MaxResponseBytes : 10 * 1024 * 1024;
        var results = new List<IReadOnlyDictionary<string, object?>>(Math.Min(Math.Max(expectedRows, 16), 1024));

        int fieldCount = reader.FieldCount;
        var columnNames = new string[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            columnNames[i] = reader.GetName(i);
        }

        long estimatedBytes = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(fieldCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fieldCount; i++)
            {
                var rawVal = ReadValue(reader, i, columnNames[i]);
                var normalized = NormalizeReadValue(rawVal, columnNames[i]);
                if (_maskingProvider != null && gatewayHmacColumns.TryGetValue(columnNames[i], out var hmacRule))
                {
                    normalized = _maskingProvider.MaskValue(columnNames[i], normalized, hmacRule);
                }
                row[columnNames[i]] = normalized;
                estimatedBytes += EstimateSerializedBytes(columnNames[i], normalized);
            }

            if (estimatedBytes > maxBytes)
            {
                throw new Autheris.Domain.Exceptions.GatewaySecurityException(
                    $"Response size exceeds the configured limit of {maxBytes} bytes.", "RESPONSE_TOO_LARGE");
            }
            results.Add(row);
        }

        return results;
    }

    private static object? ReadValue(DbDataReader reader, int ordinal, string columnName)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or InvalidCastException or NotSupportedException)
        {
            // SqlClient throws these for UDT columns (geography, geometry, hierarchyid) when Microsoft.SqlServer.Types is missing.
            string typeName;
            try
            {
                typeName = reader.GetDataTypeName(ordinal);
            }
            catch (Exception)
            {
                typeName = "unknown";
            }
            throw new Autheris.Domain.Exceptions.GatewayUnsupportedColumnTypeException(columnName, typeName, ex);
        }
    }

    /// <summary>Same estimate as the final size check in GatewayExecutionService (UTF-16 lengths, 16 bytes per scalar).</summary>
    private static long EstimateSerializedBytes(string columnName, object? value) =>
        columnName.Length * 2L + value switch
        {
            null => 0,
            string s => s.Length * 2L,
            byte[] b => b.Length,
            _ => 16
        };

    /// <summary>
    /// WP-D4 (F-1): the one mapping from a catalog dialect to the mask SQL dialect. Fail-closed: a dialect without
    /// an explicit mapping (any other or undefined value) throws instead of receiving PostgreSQL mask SQL.
    /// </summary>
    public static TrinoSqlEngine.TargetSqlDialect ToMaskTargetDialect(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.PostgreSql => TrinoSqlEngine.TargetSqlDialect.PostgreSql,
        DatabaseDialect.SqlServer => TrinoSqlEngine.TargetSqlDialect.SqlServer,
        DatabaseDialect.Sqlite => TrinoSqlEngine.TargetSqlDialect.Sqlite,
        DatabaseDialect.Oracle => TrinoSqlEngine.TargetSqlDialect.Oracle,
        _ => throw new NotSupportedException($"No mask SQL mapping exists for dialect '{dialect}'.")
    };

    public static string BuildMaskedColumnProjection(string columnName, string? dataType, DatabaseDialect dialect, TableMetadata tableMeta)
    {
        ArgumentNullException.ThrowIfNull(tableMeta);
        DatabaseDialectExtensions.ValidateIdentifier(columnName);
        var quotedCol = dialect.QuoteIdentifier(columnName);
        string maskExpr;

        if (tableMeta.ColumnMaskingRules.TryGetValue(columnName, out var rule))
        {
            var ruleType = rule.RuleType?.ToUpperInvariant() ?? "REDACT";
            var effectiveDataType = dataType ?? tableMeta.Columns?.FirstOrDefault(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase))?.DataType;
            if (ruleType == "NULLIFY" || (ruleType == "REDACT" && IsNumericOrTemporalType(effectiveDataType)))
            {
                maskExpr = "NULL";
            }
            else if (ruleType == "GEO_JITTER")
            {
                var targetDialect = ToMaskTargetDialect(dialect);
                maskExpr = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor.BuildDialectMaskExpression(
                    columnName,
                    "GEO_JITTER",
                    targetDialect,
                    decimals: rule.Decimals ?? 2);
            }
            else if (ruleType == "PARTIAL_MASK")
            {
                var targetDialect = ToMaskTargetDialect(dialect);
                maskExpr = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor.BuildDialectMaskExpression(
                    columnName,
                    "PARTIAL_MASK",
                    targetDialect,
                    keepPrefix: rule.KeepPrefix ?? 1,
                    keepSuffix: rule.KeepSuffix ?? 0,
                    maskChar: rule.MaskChar ?? '*',
                    fixedLength: rule.FixedLength ?? false);
            }
            else if (IsHmacRule(rule))
            {
                // SEC H-13: No unkeyed in-DB hash. HMAC columns are pseudonymized in the gateway (IColumnMaskingProvider);
                // without a masking provider the column is redacted (fail-closed).
                maskExpr = "'***'";
            }
            else if (!string.IsNullOrWhiteSpace(rule.Replacement))
            {
                var prefix = dialect == DatabaseDialect.SqlServer ? "N" : string.Empty;
                maskExpr = $"{prefix}'{dialect.EscapeSqlLiteral(rule.Replacement)}'";
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

    private static bool IsHmacRule(MaskingRule rule) => rule.IsHmac;

    private static bool IsNumericOrTemporalType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return false;
        var dt = dataType.Trim().ToLowerInvariant();
        if (dt.Contains('(')) dt = dt[..dt.IndexOf('(')].Trim();
        return dt is "int" or "integer" or "bigint" or "smallint" or "tinyint" or "numeric" or "decimal"
            or "money" or "smallmoney" or "real" or "float" or "double precision" or "double"
            or "bit" or "bool" or "boolean" or "date" or "datetime" or "datetime2" or "smalldatetime"
            or "timestamp" or "timestamptz" or "time" or "uniqueidentifier" or "uuid";
    }

    /// <summary>
    /// SEC H-13: Derives a tenant-scoped HMAC key id so pseudonyms cannot be correlated across tenants.
    /// The actual key derivation (HMAC over the master secret) happens inside <see cref="IColumnMaskingProvider"/>.
    /// </summary>
    private static MaskingRule CreateTenantScopedHmacRule(MaskingRule rule, string tenant, string? defaultKeyId) =>
        MaskingRule.CreateTenantScopedHmacRule(rule, tenant, defaultKeyId);

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
                throw new SecurityException($"Binary column '{columnName ?? "unknown"}' exceeds the maximum allowed size of {MaxAllowedBinaryBytes / (1024 * 1024)} MB.");
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

        if (context.Items.TryGetValue(TableQueryItems.Filter, out var filterObj) &&
            filterObj is TableFilterClause filterClause &&
            filterClause.ReferencedColumns.Any(c => c.Equals("parent_id", StringComparison.OrdinalIgnoreCase) || c.Equals("invoice_id", StringComparison.OrdinalIgnoreCase)))
        {
            var joinCol = metadata.Columns.FirstOrDefault(c => c.ColumnName.Equals("parent_id", StringComparison.OrdinalIgnoreCase))?.ColumnName
                ?? metadata.Columns.FirstOrDefault(c => c.ColumnName.Equals("invoice_id", StringComparison.OrdinalIgnoreCase))?.ColumnName
                ?? "parent_id";

            foreach (var pVal in filterClause.Parameters.Values)
            {
                var parentId = pVal?.ToString() ?? "";
                if (string.IsNullOrEmpty(parentId)) continue;

                for (int i = 1; i <= 2; i++)
                {
                    var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["id"] = $"{parentId}-ITEM-{i}",
                        [joinCol] = parentId,
                        ["product_name"] = $"Enterprise License Pack {i}",
                        ["price"] = 1250.00m * i,
                        ["sensitive_note"] = $"Confidential spec for item {i} of invoice {parentId}"
                    };
                    rows.Add(dict);
                }
            }
            return rows;
        }

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
