namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

public sealed class GovernedSqlExecutionService : IGovernedSqlExecutionService
{
    /// <summary>
    /// SEC H-13/M-10: Prefix reserved for gateway-internal command parameters (masking keys, row-filter parameters).
    /// Neither the raw SQL nor client-supplied parameters may use it.
    /// </summary>
    internal const string InternalParameterPrefix = "__gql_";


    /// <summary>
    /// SQ-01/02/10/11/13: Token checks applied already during the analysis step (dollar quoting is decided per dialect
    /// in the rewrite step, see RlsOptions below).
    /// </summary>
    private static readonly SqlTokenSecurityOptions AnalysisTokenOptions = new()
    {
        RejectComments = true,
        RejectBackslashInStrings = true,
        RejectEscapedStringLiterals = true,
        RejectNonAsciiIdentifiers = true,
        RejectDotsInQuotedIdentifiers = true,
        RejectTimeTravelQueries = true
    };

    private readonly ISqlEngine _sqlEngine;
    private readonly ICompiledSqlQueryPlanCache? _planCache;
    private readonly ISqlSecurityValidator _sqlSecurityValidator;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IPolicyEnforcementService? _policyEnforcement;
    private readonly IConsentResolutionService? _consentResolution;
    private readonly ITableMetadataRepository? _tableRepository;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly ISqlConnectionFactory? _connectionFactory;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<GovernedSqlExecutionService>? _logger;
    private readonly Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator? _rebacEvaluator;
    private readonly IConsentCacheService? _consentCache;
    private readonly IConsentRepository? _consentRepository;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly ITableReadConcurrencyGate? _concurrencyGate;
    private readonly IDbSessionContextInitializer _sessionInitializer;

    private const int ThrottledRetryAfterSeconds = 2;

    private byte[]? _masterHmacKey;
    private bool _masterHmacKeyResolved;

    public GovernedSqlExecutionService(
        IOptions<GatewayOptions> options,
        IPolicyEnforcementService? policyEnforcement = null,
        IConsentResolutionService? consentResolution = null,
        ITableMetadataRepository? tableRepository = null,
        IAuditLogRepository? auditLogRepository = null,
        ISqlConnectionFactory? connectionFactory = null,
        IClientIpResolver? clientIpResolver = null,
        IHostEnvironment? environment = null,
        ILogger<GovernedSqlExecutionService>? logger = null,
        IConsentRepository? consentRepository = null,
        IKeyVaultSecretProvider? secretProvider = null,
        ISqlEngine? sqlEngine = null,
        ICompiledSqlQueryPlanCache? planCache = null,
        ISqlSecurityValidator? sqlSecurityValidator = null,
        ITableReadConcurrencyGate? concurrencyGate = null,
        IDbSessionContextInitializer? sessionInitializer = null,
        Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator? rebacEvaluator = null,
        IConsentCacheService? consentCache = null,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null)
    {
        _mandatoryFilters = mandatoryFilters;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _policyEnforcement = policyEnforcement;
        _consentResolution = consentResolution;
        _tableRepository = tableRepository;
        _auditLogRepository = auditLogRepository;
        _connectionFactory = connectionFactory;
        _clientIpResolver = clientIpResolver;
        _environment = environment;
        _logger = logger;
        _consentRepository = consentRepository;
        _secretProvider = secretProvider;
        _sqlEngine = sqlEngine ?? FastSqlEngine.Default;
        _planCache = planCache;
        _sqlSecurityValidator = sqlSecurityValidator ?? new DefaultSqlSecurityValidator();
        _concurrencyGate = concurrencyGate;
        _sessionInitializer = sessionInitializer ?? new DbSessionContextInitializer();
        _rebacEvaluator = rebacEvaluator;
        _consentCache = consentCache;
    }

    public async Task<string> RewriteSqlAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        var rewrite = await RewriteCoreAsync(rawSql, user, tenantId, dataSourceName: null, dmlContext: null, ct).ConfigureAwait(false);
        return rewrite.Sql;
    }

    private async Task<GovernedRewrite> RewriteCoreAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        string? dataSourceName,
        DmlAuditContext? dmlContext,
        CancellationToken ct,
        SqlRowLimit? rowLimit = null,
        bool probeExtraRow = false)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
            throw new ArgumentException("SQL query cannot be empty or whitespace.", nameof(rawSql));
        }

        ArgumentNullException.ThrowIfNull(user);

        var webSqlOptions = _options.Value.WebSql;
        if (!webSqlOptions.Enabled)
        {
            throw new WebSqlPolicyException("WebSQL execution is disabled by gateway configuration.");
        }

        if (rawSql.Length > webSqlOptions.MaxQueryLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rawSql),
                $"SQL query length ({rawSql.Length}) exceeds the maximum allowed limit of {webSqlOptions.MaxQueryLength} characters.");
        }

        // SEC H-13: Gateway-internal parameter names (masking keys) must never be addressable from client SQL.
        if (rawSql.Contains(InternalParameterPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new WebSqlPolicyException("The SQL statement references a reserved gateway identifier.");
        }

        // 1. AST Analysis
        SqlQueryMetadata metadata;
        try
        {
            metadata = _sqlEngine.Analyze(rawSql.AsMemory(), AnalysisTokenOptions, ct);
        }
        catch (WebSqlPolicyException)
        {
            throw;
        }
        catch (SecurityException secEx)
        {
            _logger?.LogWarning(secEx, "WebSQL statement rejected by SQL analyzer.");
            throw new WebSqlPolicyException("The SQL statement uses a construct that is not permitted by the WebSQL security policy.", secEx);
        }
        catch (OperationCanceledException timeoutEx) when (!ct.IsCancellationRequested && timeoutEx.InnerException is TimeoutException)
        {
            // SQ-08: parse time budget exceeded (ParseCanceledException with inner TimeoutException)
            throw new WebSqlPolicyException("The SQL statement exceeded the parser time budget.", timeoutEx);
        }
        catch (OperationCanceledException parseEx) when (!ct.IsCancellationRequested)
        {
            // ANTLR ParseCanceledException derives from OperationCanceledException
            throw new ArgumentException("The SQL statement could not be parsed.", nameof(rawSql), parseEx);
        }

        // 2. Validate Statement Type (Reject DDL, allow DML only if explicitly enabled AND authorized)
        if (metadata.StatementType == SqlStatementType.Ddl)
        {
            throw new WebSqlPolicyException("DDL statements (CREATE, DROP, ALTER, TRUNCATE, GRANT, REVOKE) are strictly forbidden in WebSQL.");
        }

        bool isDml = metadata.StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete;
        if (isDml && dmlContext != null)
        {
            // Captured before any DML policy check so that rejected DML statements are audited as well.
            dmlContext.StatementType = metadata.StatementType;
            dmlContext.Tables = metadata.ReferencedTables
                .Select(t => t.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (metadata.StatementType != SqlStatementType.Select && (!isDml || !_options.Value.IsWebSqlDmlAllowed))
        {
            throw new WebSqlPolicyException(
                $"WebSQL only permits read-only SELECT statements by default. Received '{metadata.StatementType}' operation. DML must be explicitly enabled via configuration.");
        }

        if (isDml)
        {
            // SEC M-20: Consent and Casbin only grant 'read'. A DML statement therefore additionally requires an
            // explicitly configured writer role; without one, DML is rejected (fail-closed).
            // Finding 3.3 / R13: a read-only token (agent scope, app-only) never writes, whatever its roles.
            if (user.IsReadOnly())
            {
                throw new WebSqlPolicyException("This token only permits read access; DML statements are rejected.");
            }

            var writerRoles = webSqlOptions.DmlWriterRoles;
            bool isWriter = writerRoles != null && writerRoles.Any(r => !string.IsNullOrWhiteSpace(r) && user.IsInRole(r));
            if (!isWriter)
            {
                isWriter = writerRoles != null && user.GetUserRoles().Overlaps(writerRoles.Where(r => !string.IsNullOrWhiteSpace(r)));
            }

            if (!isWriter)
            {
                throw new WebSqlPolicyException("WebSQL DML requires an authorized writer role (WebSql.DmlWriterRoles). Consent and ABAC policies only grant read access.");
            }

            // TODO: per-table write permission via a Casbin action "write" (in addition to DmlWriterRoles) is planned
            // for a later iteration; today the ABAC evaluation below only receives gql.action = "write" as attribute.
        }

        // 3. Danger Bypass: If WebSQL governance is dangerously bypassed in dev/test, return raw SQL
        //    (DANGER: Development-only; this also skips the unfiltered-DML guardrail of the RLS rewriter).
        if (_options.Value.IsWebSqlGovernanceBypassed)
        {
            _logger?.LogWarning("[DANGER] WebSQL governance bypass is active! Query will be executed without RLS or AST masking.");
            return new GovernedRewrite(rawSql, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), Array.Empty<TableIdentifier>(), dataSourceName ?? _options.Value.WebSql.DefaultDataSourceName);
        }

        // SEC C-01: RLS, masking and policies attach to physical table nodes. A statement without any governed table
        // (e.g. SELECT query_to_xml('SELECT * FROM hr.salaries', ...)) would escape all of them and is rejected.
        if (metadata.ReferencedTables.Count == 0)
        {
            throw new WebSqlPolicyException("WebSQL statements must reference at least one governed catalog table. Table-less statements (e.g. standalone function calls) are not permitted.");
        }

        // SEC C-01/H-14: Early function policy check on the analysis result (the RLS rewriter enforces the same policy
        // again via RlsOptions.EnforceFunctionPolicy). Table functions, WITH SESSION and inline functions are rejected.
        if (metadata.TableFunctionCalls is { Count: > 0 } || metadata.HasSessionProperties || metadata.HasInlineFunctionDefinitions)
        {
            throw new WebSqlPolicyException("Table functions, session properties and inline function definitions are not permitted in WebSQL.");
        }

        if (metadata.FunctionCalls is { Count: > 0 })
        {
            var functionPolicy = new RlsOptions { EnforceFunctionPolicy = true };
            foreach (var functionName in metadata.FunctionCalls)
            {
                if (!SqlFunctionPolicy.IsFunctionAllowed(functionName, functionPolicy))
                {
                    throw new WebSqlPolicyException($"Function '{functionName}' is not permitted in WebSQL.");
                }
            }
        }

        // 4. Resolve identity
        bool consentBypassed = _options.Value.IsConsentBypassed;
        var userSidNullable = user.GetUserSid();
        if (userSidNullable == null && !consentBypassed)
        {
            throw new WebSqlPolicyException("An authenticated user identity is required for WebSQL.");
        }

        var userSid = userSidNullable ?? new Sid("anonymous");
        var groupSids = user.GetGroupSids();
        var roles = user.GetUserRoles();
        var clientIp = ResolveClientIp(user);

        // SEC M-20: Make the requested action visible to ABAC sub-rules (applied after claims so it cannot be spoofed).
        var actionAttribute = new Dictionary<string, object?>
        {
            ["gql.action"] = isDml ? "write" : "read",
            ["action"] = isDml ? "write" : "read"
        };

        // SQ-09 / Trino Compatibility: In Trino queries, 3-part names (catalog.schema.table) are canonical.
        // Catalog auto-inference and cross-catalog validation:
        var distinctCatalogs = metadata.ReferencedTables
            .Where(t => !string.IsNullOrWhiteSpace(t.Catalog))
            .Select(t => t.Catalog!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctCatalogs.Count > 1)
        {
            throw new WebSqlPolicyException("Cross-catalog queries across multiple data sources are not supported in WebSQL.");
        }

        string effectiveDataSourceName;
        if (!string.IsNullOrWhiteSpace(dataSourceName))
        {
            effectiveDataSourceName = dataSourceName;
            if (distinctCatalogs.Count == 1 && !string.Equals(distinctCatalogs[0], dataSourceName, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("WebSQL rejected statement: query catalog '{Catalog}' does not match data source '{DataSource}'.", distinctCatalogs[0], dataSourceName);
                throw new WebSqlPolicyException($"Query catalog '{distinctCatalogs[0]}' does not match requested data source '{dataSourceName}'.");
            }
        }
        else if (distinctCatalogs.Count == 1)
        {
            effectiveDataSourceName = ResolveAllowedDataSource(distinctCatalogs[0], tenantId);
        }
        else
        {
            effectiveDataSourceName = ResolveAllowedDataSource(null, tenantId);
        }

        // 5. Resolve RLS filters and Column Masking for all referenced physical tables
        var tableRlsFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tablesWithoutRls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableMaskingExpressions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var tableColumnsMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var tablesWithConsentRowFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tablesWithMaskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableTenantColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? primaryTenantColumn = null;
        var internalParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var hmacKeyParameterNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var accessedTables = new List<TableIdentifier>();
        var virtualFilters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var accessedTableSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // SEC P-05: The SQL dialect comes from the data source configuration (the provider the connection factory will
        // actually use). Without a configured connection (synthetic dev/test path) the catalog dialect of the first table
        // is used. Every referenced table must have exactly this dialect.
        DatabaseDialect? targetDatabaseDialect = ResolveConnectionDialect(effectiveDataSourceName);

        // SQL-2: the policy maps below are keyed case-insensitively. Two references that fold to the same key but are
        // spelled differently may address different physical relations (PostgreSQL quoted identifiers, SQL Server with
        // a case-sensitive collation), and their policies would overwrite each other -> reject.
        var referenceSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in metadata.ReferencedTables)
        {
            if (referenceSpellings.TryGetValue(target.FullName, out var firstSpelling) &&
                !string.Equals(firstSpelling, target.FullName, StringComparison.Ordinal))
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: referenced with differing letter case ('{First}').", target.FullName, firstSpelling);
                throw TableDenied(target);
            }

            referenceSpellings[target.FullName] = target.FullName;
        }

        var tableNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var schemaTableCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in metadata.ReferencedTables)
        {
            tableNameCounts[t.TableName] = tableNameCounts.GetValueOrDefault(t.TableName) + 1;
            if (!string.IsNullOrWhiteSpace(t.Schema))
            {
                var st = $"{t.Schema}.{t.TableName}";
                schemaTableCounts[st] = schemaTableCounts.GetValueOrDefault(st) + 1;
            }
        }

        void RegisterTableLookup<T>(IDictionary<string, T> dict, TableAccessTarget target, TableIdentifier resolvedId, T value)
        {
            dict[target.FullName] = value;
            dict[resolvedId.ToQualifiedName()] = value;

            // SR15-02: Only register unqualified table name if it is unique among all referenced tables in this statement
            if (tableNameCounts.TryGetValue(target.TableName, out var count) && count == 1)
            {
                dict[target.TableName] = value;
            }

            if (!string.IsNullOrWhiteSpace(target.Schema))
            {
                var st = $"{target.Schema}.{target.TableName}";
                if (schemaTableCounts.TryGetValue(st, out var sCount) && sCount == 1)
                {
                    dict[st] = value;
                }
            }
        }

        void RegisterTableSet(ISet<string> set, TableAccessTarget target, TableIdentifier resolvedId)
        {
            set.Add(target.FullName);
            set.Add(resolvedId.ToQualifiedName());

            // SR15-02: Only register unqualified table name if it is unique among all referenced tables in this statement
            if (tableNameCounts.TryGetValue(target.TableName, out var count) && count == 1)
            {
                set.Add(target.TableName);
            }

            if (!string.IsNullOrWhiteSpace(target.Schema))
            {
                var st = $"{target.Schema}.{target.TableName}";
                if (schemaTableCounts.TryGetValue(st, out var sCount) && sCount == 1)
                {
                    set.Add(st);
                }
            }
        }

        foreach (var target in metadata.ReferencedTables)
        {
            // SEC C-03: Tables without catalog metadata (or without column metadata) cannot be governed -> reject.
            TableIdentifier tableId = ResolveTableIdentifier(target, effectiveDataSourceName);
            TableIdentifier resolvedId = tableId;
            TableMetadata? tableMeta = null;
            if (_tableRepository != null)
            {
                tableMeta = await _tableRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);

                // An unqualified name is catalogued under "public" by convention, or under the schema the backend
                // itself resolves it to (SQLite "main", SQL Server "dbo"). Both entries for one physical table would
                // make the applicable policy ambiguous -> reject.
                string? backendDefaultSchema = targetDatabaseDialect switch
                {
                    DatabaseDialect.Sqlite => "main",
                    DatabaseDialect.SqlServer => "dbo",
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(target.Schema) && backendDefaultSchema != null &&
                    !TableIdentifier.TryParse(target.FullName, out _))
                {
                    var backendId = new TableIdentifier(tableId.Domain, backendDefaultSchema, tableId.TableName);
                    var backendMeta = await _tableRepository.GetTableMetadataAsync(backendId, ct).ConfigureAwait(false);
                    if (backendMeta != null && tableMeta != null)
                    {
                        _logger?.LogWarning("WebSQL rejected table {Table}: catalogued under both '{PublicId}' and '{BackendId}'.", target.FullName, tableId, backendId);
                        throw TableDenied(target);
                    }

                    if (backendMeta != null)
                    {
                        tableMeta = backendMeta;
                        tableId = backendId;
                        resolvedId = backendId;
                    }
                }

                if (tableMeta == null && !string.Equals(tableId.Domain, "default", StringComparison.OrdinalIgnoreCase))
                {
                    resolvedId = new TableIdentifier("default", tableId.Schema, tableId.TableName);
                    tableMeta = await _tableRepository.GetTableMetadataAsync(resolvedId, ct).ConfigureAwait(false);
                }
            }

            if (tableMeta == null || tableMeta.Columns.Count == 0)
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: no catalog metadata registered.", target.FullName);
                throw TableDenied(target);
            }

            // SEC P-05: Only dialects with a dedicated rewrite (PostgreSQL, SQL Server, SQLite) are supported (fail-closed).
            if (!IsWebSqlSupportedDialect(tableMeta.Dialect))
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: SQL dialect {Dialect} is not supported by WebSQL.", target.FullName, tableMeta.Dialect);
                throw new WebSqlPolicyException("WebSQL only supports tables of PostgreSQL, SQL Server and SQLite data sources.");
            }

            if (targetDatabaseDialect == null)
            {
                targetDatabaseDialect = tableMeta.Dialect;
            }
            else if (targetDatabaseDialect.Value != tableMeta.Dialect)
            {
                _logger?.LogWarning(
                    "WebSQL rejected table {Table}: catalog dialect {TableDialect} does not match the data source dialect {DataSourceDialect}.",
                    target.FullName,
                    tableMeta.Dialect,
                    targetDatabaseDialect.Value);
                throw TableDenied(target);
            }

            // SEC C-03 / SQ-09: A catalog table bound to a specific data source must only be queried through that source.
            if (!string.IsNullOrWhiteSpace(tableMeta.Table.SourceName) &&
                !string.Equals(tableMeta.Table.SourceName, effectiveDataSourceName, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: catalog source does not match the requested data source.", target.FullName);
                throw TableDenied(target);
            }

            // SQ-09 / SQL-5 / Trino Compatibility: In Trino queries, 3-part names (catalog.schema.table) are canonical.
            // The catalog part identifies the logical data source and is validated against the active data source.
            // When rewriting to backend SQL dialects (PostgreSQL, SQL Server, SQLite), the catalog prefix is stripped
            // by the target dialect generator and RlsListener to prevent cross-database/physical collision errors.
            if (!string.IsNullOrWhiteSpace(target.Catalog))
            {
                if (!string.Equals(target.Catalog, effectiveDataSourceName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("WebSQL rejected table {Table}: catalog '{Catalog}' does not match active data source '{DataSource}'.",
                        target.FullName, target.Catalog, effectiveDataSourceName);
                    throw TableDenied(target);
                }
            }

            // RR-L5-02: PostgreSQL identifiers are case-sensitive when quoted and fold to lower case when unquoted.
            // The physical relation addressed by the statement must be exactly the catalogued one.
            if (targetDatabaseDialect == DatabaseDialect.PostgreSql && !MatchesPostgreSqlCatalogName(target, tableMeta.Identifier))
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: PostgreSQL identifier case does not match the catalog entry.", target.FullName);
                throw TableDenied(target);
            }

            var colList = tableMeta.Columns.Select(c => c.ColumnName).ToList();
            RegisterTableLookup(tableColumnsMap, target, resolvedId, colList);

            if (accessedTableSet.Add(resolvedId.ToQualifiedName()))
            {
                accessedTables.Add(resolvedId);
            }

            // SEC C-03 / Architecture 1: the shared access decision (ReBAC on query paths, consents with the decision
            // cache, Casbin as an additional restriction). The merged row filter is validated below.
            if (!consentBypassed && (_consentRepository == null || _consentResolution == null))
            {
                _logger?.LogError("WebSQL cannot evaluate consents (consent services not available); denying access (fail-closed).");
                throw TableDenied(target);
            }

            var decision = consentBypassed && (_consentRepository == null || _consentResolution == null)
                ? TableAccessDecision.Allowed(resolvedId, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true)
                : await AccessPolicy().DecideAsync(
                    new TableAccessQuery(userSid, tenantId, groupSids, roles, tableMeta, user.Claims, colList, RebacEnforcement.QueryPaths, clientIp, actionAttribute),
                    ct).ConfigureAwait(false);

            if (!decision.IsAllowed)
            {
                _logger?.LogWarning("WebSQL access to table {Table} denied: {Reasons}", target.FullName, string.Join("; ", decision.DeniedReasons));
                throw TableDenied(target);
            }

            // Row-level security: tenant isolation (defense in depth) AND consent/ABAC row filters
            var rlsParts = new List<string>(2);
            // Review E-5: any usual spelling of the tenant column is honoured; unusual names are not written into SQL.
            string? tenantColumn = TableMetadata.RequireTenantColumnOrThrow(
                tableMeta,
                _options.Value.DataSources?.RequireTenantColumn == true,
                _options.Value.DataSources?.TenantColumnExemptTables);
            if (tenantColumn != null)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(tenantColumn, "^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
                {
                    throw new InvalidOperationException($"The tenant column '{tenantColumn}' of {target.FullName} is not a plain identifier; the query is refused (fail-closed).");
                }

                primaryTenantColumn ??= tenantColumn;
                RegisterTableLookup(tableTenantColumns, target, resolvedId, tenantColumn);

                rlsParts.Add($"{tenantColumn} = '{tenantId.Value.Replace("'", "''")}'");
            }

            if (decision.AppliedVirtualFilters is { Count: > 0 } applied)
            {
                RegisterTableLookup(virtualFilters, target, resolvedId, applied);
            }

            if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
            {
                _sqlSecurityValidator.ValidateRowFilter(decision);
                rlsParts.Add($"({decision.CombinedRowFilterSql})");
                AddInternalRowFilterParameters(decision.RowFilterParameters, internalParameters);
                RegisterTableSet(tablesWithConsentRowFilter, target, resolvedId);
            }

            if (rlsParts.Count > 0)
            {
                var rlsFilter = string.Join(" AND ", rlsParts);
                RegisterTableLookup(tableRlsFilters, target, resolvedId, rlsFilter);
            }
            else
            {
                RegisterTableSet(tablesWithoutRls, target, resolvedId);
            }

            // Column projection / masking (SEC C-03/H-10: shared effective access function, catalog-sensitive -> Mask unless explicit Clear)
            var columnMasks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in tableMeta.Columns)
            {
                var lvl = decision.GetEffectiveColumnAccess(col.ColumnName, tableMeta);
                bool isEffectiveHmac = false;

                if (lvl == ColumnAccessLevel.Deny)
                {
                    columnMasks[col.ColumnName] = "NULL";
                }
                else if (lvl == ColumnAccessLevel.Mask)
                {
                    columnMasks[col.ColumnName] = GetMaskExpressionForRule(col.ColumnName, tableMeta, tenantId, internalParameters, hmacKeyParameterNames, out isEffectiveHmac);
                }

                // SEC-JOIN-01 / SQL-6: Zero-Trust Guardrail: Check if any statically redacted column is used as a JOIN predicate for this table
                if (lvl != ColumnAccessLevel.Clear && !isEffectiveHmac)
                {
                    bool isUsedInJoin = false;
                    if (metadata.JoinColumnReferences != null && metadata.JoinColumnReferences.Count > 0)
                    {
                        isUsedInJoin = metadata.JoinColumnReferences.Any(jc => ReferencesColumn(jc.TableOrAlias, jc.ColumnName, col.ColumnName, target));
                    }

                    if (isUsedInJoin)
                    {
                        string ruleDesc = tableMeta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var mRule)
                            ? mRule.RuleType ?? "REDACT"
                            : (lvl == ColumnAccessLevel.Deny ? "DENY" : "ABAC_MASK");

                        throw new WebSqlPolicyException(
                            $"Security Policy Violation: Column '{col.ColumnName}' in table '{target.FullName}' is protected by static redaction ('{ruleDesc}') and cannot be used in a relational JOIN predicate. Joining on static constants produces false Cartesian cross-products and enables side-channel join inference attacks. Configure deterministic HMAC pseudonymization (RuleType = 'HMAC') or join on surrogate foreign keys (e.g. ID).");
                    }

                    // SEC-FILTER-01 / Befund 3.6: Zero-Trust Guardrail: Check if any masked or denied column is used in WHERE / HAVING / ORDER BY
                    bool isUsedInFilter = false;
                    if (metadata.FilterColumnReferences != null && metadata.FilterColumnReferences.Count > 0)
                    {
                        isUsedInFilter = metadata.FilterColumnReferences.Any(fc => ReferencesColumn(fc.TableOrAlias, fc.ColumnName, col.ColumnName, target));
                    }

                    if (isUsedInFilter)
                    {
                        string ruleDesc = tableMeta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var mRule)
                            ? mRule.RuleType ?? "REDACT"
                            : (lvl == ColumnAccessLevel.Deny ? "DENY" : "ABAC_MASK");

                        throw new WebSqlPolicyException(
                            $"Security Policy Violation: Column '{col.ColumnName}' in table '{target.FullName}' is protected by static redaction or access policy ('{ruleDesc}') and cannot be used in a filter predicate (WHERE/HAVING/ORDER BY). Filtering or sorting on masked or denied columns would run against the redacted value and is forbidden to prevent oracle inference attacks.");
                    }
                }
            }

            if (columnMasks.Count > 0)
            {
                RegisterTableLookup(tableMaskingExpressions, target, resolvedId, columnMasks);
                RegisterTableSet(tablesWithMaskedColumns, target, resolvedId);
            }
        }

        // 6. Construct RlsOptions
        // The transport (Trino, Parquet, SQL endpoints, Arrow, Flight SQL) may carry its own configured row limit.
        long maxRows = (rowLimit ?? SqlRowLimit.For(webSqlOptions))
            .Effective(metadata.HasExplicitLimit ? metadata.ExplicitLimitValue : null);

        // WebSQL findings 2.4: executions read one probe row beyond the limit; RowLimitedDataReader drops it and reports
        // whether rows were cut. A client's own LIMIT up to the maximum stays below the probe and is never "truncated".
        long deliveredRowLimit = probeExtraRow && !isDml && maxRows > 0 ? maxRows : 0;
        long enforcedMaxRows = deliveredRowLimit > 0 ? maxRows + 1 : maxRows;

        // SEC C-03: Any table name the rewriter encounters that was not resolved above is filtered to the empty set (fail-closed).
        const string denyAllFilter = "1 = 0";

        // SEC P-05: no fallback to ANSI for unknown dialects (fail-closed).
        TargetSqlDialect targetSqlDialect = targetDatabaseDialect switch
        {
            DatabaseDialect.PostgreSql => TargetSqlDialect.PostgreSql,
            DatabaseDialect.SqlServer => TargetSqlDialect.SqlServer,
            DatabaseDialect.Sqlite => TargetSqlDialect.Sqlite,
            _ => throw new WebSqlPolicyException("WebSQL only supports tables of PostgreSQL, SQL Server and SQLite data sources.")
        };

        // SQ-06 / SEC P-02: Function allowlist of the target dialect plus WebSql.AdditionalAllowedFunctions.
        // Denylisted functions are never allowed (SqlFunctionAllowlists.Build and SqlFunctionPolicy enforce this).
        IReadOnlySet<string> allowedFunctions = SqlFunctionAllowlists.Build(targetSqlDialect, webSqlOptions.AdditionalAllowedFunctions);
        if (metadata.FunctionCalls is { Count: > 0 })
        {
            var allowlistPolicy = new RlsOptions { EnforceFunctionPolicy = true, AllowedFunctions = allowedFunctions };
            foreach (var functionName in metadata.FunctionCalls)
            {
                if (!SqlFunctionPolicy.IsFunctionAllowed(functionName, allowlistPolicy))
                {
                    throw new WebSqlPolicyException($"Function '{functionName}' is not permitted in WebSQL.");
                }
            }
        }

        var policyProvider = new DefaultRlsPolicyProvider(
            defaultFilter: denyAllFilter,
            predicate: tbl => !tablesWithoutRls.Contains(tbl),
            filterFunc: tbl => tableRlsFilters.TryGetValue(tbl, out var f) ? f : denyAllFilter)
        {
            FallbackToSimpleName = false
        };

        var maskingProvider = new DefaultColumnMaskingPolicyProvider(
            hasMaskPredicate: (tbl, col) => tableMaskingExpressions.TryGetValue(tbl, out var dict) && dict.ContainsKey(col),
            maskExpressionProvider: (tbl, col) => tableMaskingExpressions.TryGetValue(tbl, out var dict) && dict.TryGetValue(col, out var expr) ? expr : "'***'")
        {
            FallbackToSimpleName = false
        };

        var rlsOptions = new RlsOptions
        {
            AppendTableAlias = true,
            EnforcedMaxRows = enforcedMaxRows,
            EnforceReadOnlyQueries = !isDml,
            TargetDialect = targetSqlDialect,
            // SQ-01 & SQ-02: Strict parser / lexer checks for governed execution
            RejectComments = true,
            RejectBackslashInStrings = true,
            RejectEscapedStringLiterals = true,
            // SQ-02: dollar quoting only matches the backend lexer on PostgreSQL (SQL Server/SQLite lex '$' differently).
            RejectDollarQuoting = targetSqlDialect != TargetSqlDialect.PostgreSql,
            // SQL-1: SQL Server/SQLite lex [...] as quoted identifier; the Trino grammar treats it as array syntax.
            RejectBracketLexerDifferentials = targetSqlDialect is TargetSqlDialect.SqlServer or TargetSqlDialect.Sqlite,
            // SQ-10/SQ-11/SQ-13 (SEC P-06): set explicitly, independent of library defaults
            RejectNonAsciiIdentifiers = true,
            RejectDotsInQuotedIdentifiers = true,
            RejectTimeTravelQueries = true,
            // SQ-03 & SQ-07: Guardrails for DML
            RejectConsentFilteredInsert = true,
            RejectWholeRowReferencesInDml = true,
            TablesWithConsentRowFilter = tablesWithConsentRowFilter,
            TablesWithMaskedColumns = tablesWithMaskedColumns,
            // SEC M-20: WITH CHECK against the caller's tenant (not the library default) and explicit tenant column on INSERT
            ExpectedTenantValue = tenantId.Value,
            TenantColumnName = primaryTenantColumn ?? "tenant_id",
            TableTenantColumns = tableTenantColumns,
            RequireTenantColumnInInsert = true,
            // SEC C-01/H-14/H-15: Function denylist, no table functions / inline functions, no masked columns in DML
            EnforceFunctionPolicy = true,
            AllowedFunctions = allowedFunctions,
            AllowInlineFunctionDefinitions = false,
            RejectMaskedColumnsInDml = true,
            // DML guardrail: UPDATE/DELETE without WHERE or with a trivially true WHERE are rejected (original statement).
            RejectUnfilteredDml = true,
            PolicyProvider = policyProvider,
            TableColumnsProvider = tbl => tableColumnsMap.TryGetValue(tbl, out var cols) ? cols : null,
            ColumnMaskingProvider = maskingProvider,
            // Wunsch 4: tenant and consent filters are rendered by the gateway in the target dialect and validated by
            // ValidatePredicateSql above; the AST compiler splices them in like the legacy rewriter (no client input).
            PolicyFiltersAreTargetDialectSql = true,
            RewriterEngine = _options.Value.WebSql.SqlRewriterEngine
        };

        // 7. Rewrite SQL AST (with plan cache fast-path if enabled)
        string securedSql;
        ulong queryHash = 0;
        ulong policyHash = 0;
        var planCache = _planCache;
        bool canUsePlanCache = planCache != null && targetDatabaseDialect.HasValue;

        if (canUsePlanCache && planCache != null)
        {
            queryHash = planCache.ComputeHash(rawSql.AsSpan());
            policyHash = planCache.ComputePolicyHash(
                tableRlsFilters,
                tableMaskingExpressions,
                tablesWithoutRls,
                enforcedMaxRows,
                isDml,
                webSqlOptions.SqlRewriterEngine ?? "LegacyTokenStream",
                tablesWithConsentRowFilter,
                tablesWithMaskedColumns,
                _options.Value.RowFilters.SubqueryStrategy);

            if (planCache.TryGetCompiledSql(rawSql, queryHash, targetDatabaseDialect.Value, tenantId, policyHash, out var cachedSql) && !string.IsNullOrEmpty(cachedSql))
            {
                return new GovernedRewrite(cachedSql, internalParameters, accessedTables, effectiveDataSourceName, deliveredRowLimit, virtualFilters);
            }
        }

        try
        {
            securedSql = _sqlEngine.RewriteRls(rawSql.AsMemory(), rlsOptions, ct);
            if (canUsePlanCache && planCache != null && !string.IsNullOrEmpty(securedSql))
            {
                planCache.SetCompiledSql(rawSql, queryHash, targetDatabaseDialect.Value, tenantId, policyHash, securedSql);
            }
        }
        catch (WebSqlPolicyException)
        {
            throw;
        }
        catch (UnfilteredDmlException unfilteredEx)
        {
            throw new WebSqlPolicyException(
                "UPDATE/DELETE statements in WebSQL require a restricting WHERE clause (statements without WHERE or with a trivially true condition such as 'WHERE 1=1' are rejected).",
                unfilteredEx);
        }
        catch (TrinoSqlEngine.Ast.Builder.AstBuildException astEx)
        {
            // Wunsch 4: a construct the AST compiler cannot translate is a client error (400), not a server error.
            throw new ArgumentException($"The SQL statement could not be compiled: {astEx.Message}", nameof(rawSql), astEx);
        }
        catch (SecurityException secEx)
        {
            _logger?.LogWarning(secEx, "WebSQL statement rejected by RLS rewriter.");
            throw new WebSqlPolicyException("The SQL statement violates the WebSQL row-level security policy.", secEx);
        }
        catch (OperationCanceledException timeoutEx) when (!ct.IsCancellationRequested && timeoutEx.InnerException is TimeoutException)
        {
            throw new WebSqlPolicyException("The SQL statement exceeded the parser time budget.", timeoutEx);
        }
        catch (OperationCanceledException parseEx) when (!ct.IsCancellationRequested)
        {
            throw new ArgumentException("The SQL statement could not be parsed.", nameof(rawSql), parseEx);
        }

        if (_options.Value.Logging.LogGeneratedSql)
        {
            _logger?.LogDebug("GovernedSqlExecutionService: Generated secured SQL: {SecuredSql}", securedSql);
        }

        return new GovernedRewrite(securedSql, internalParameters, accessedTables, effectiveDataSourceName, deliveredRowLimit, virtualFilters);
    }

    public async Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default)
    {
        await ExecuteCoreAsync(request, user, tenantId, rowWriter, ct).ConfigureAwait(false);
    }

    private async Task<string> ExecuteCoreAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rowWriter);

        // SEC C-03: dataSource must be on the allowlist (default data source + WebSql.AllowedDataSources),
        // restricted further by WebSql.TenantDataSourceAllowlist if the tenant has an entry.
        string? requestedDs = !string.IsNullOrWhiteSpace(request.DataSourceName)
            ? ResolveAllowedDataSource(request.DataSourceName, tenantId)
            : null;

        // SEC H-13 / SR15-01: Client parameters must not collide with gateway-internal parameters or RLS placeholders
        if (request.Parameters != null)
        {
            foreach (var paramName in request.Parameters.Keys)
            {
                var trimmed = paramName.TrimStart('@');
                if (trimmed.StartsWith(InternalParameterPrefix, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("p_rls_", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("rls_", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WebSqlPolicyException("A request parameter uses a reserved gateway parameter name.");
                }
            }
        }

        // Client parameters (@name) are not part of the Trino grammar: they are parsed as placeholder identifiers and
        // turned back into bound parameters after the rewrite.
        string sqlForRewrite = NormalizeClientParameters(request.Sql, request.Parameters, out var clientParameterNames);

        var dmlContext = new DmlAuditContext();
        GovernedRewrite rewrite;
        try
        {
            rewrite = await RewriteCoreAsync(sqlForRewrite, user, tenantId, requestedDs, dmlContext, ct, request.RowLimit, probeExtraRow: true).ConfigureAwait(false);
        }
        catch (SecurityException policyEx) when (dmlContext.IsDml)
        {
            // Rejected DML statements are recorded in the audit chain as well.
            string auditDs = requestedDs ?? _options.Value.WebSql.DefaultDataSourceName;
            await RecordDmlAuditAsync(tenantId, user, auditDs, dmlContext, "WEBSQL_DML_REJECTED", "DENY", request.Sql, affectedRows: null, reason: policyEx is WebSqlPolicyException ? policyEx.Message : "policy violation", synthetic: false, ct).ConfigureAwait(false);
            throw;
        }

        string dsName = rewrite.DataSourceName;

        string securedSql = RestoreClientParameters(rewrite.Sql, clientParameterNames);
        if (_options.Value.Logging.LogGeneratedSql)
        {
            _logger?.LogDebug("GovernedSqlExecutionService: Executing secured SQL: {SecuredSql}", securedSql);
        }

        // Audit Log Entry (secured SQL only contains parameter placeholders, never masking keys).
        // DML statements are audited separately (WEBSQL_DML_*), without SQL text that may carry literal data values.
        if (_auditLogRepository != null && !dmlContext.IsDml)
        {
            var userSid = ResolveUserSid(user);
            await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "WEBSQL_QUERY",
                ActorSid = userSid,
                TargetTable = dsName,
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    originalSql = request.Sql,
                    securedSql,
                    dataSource = dsName,
                    virtual_filters = rewrite.VirtualFilters
                })
            }, ct).ConfigureAwait(false);
        }

        // Connection resolution
        DataSourceConnectionOptions? connOptions = ResolveConnectionOptions(dsName);

        if (connOptions == null || string.IsNullOrWhiteSpace(connOptions.ConnectionString) || _connectionFactory == null)
        {
            bool isDev = _environment != null &&
                         string.Equals(_environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
            bool isExplicitlyAllowed = _options.Value.AreExternalSystemsMockedIfUnreachable;

            if (!isDev && !isExplicitlyAllowed)
            {
                throw new GatewayNotImplementedException($"No active database connection configured for data source '{dsName}'. Synthetic fallback is disabled outside Development.");
            }

            if (dmlContext.IsDml)
            {
                await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_EXECUTED", "ALLOW", securedSql, affectedRows: 0, reason: null, synthetic: true, ct).ConfigureAwait(false);
            }

            // Synthetic demo reader for testing/dev environments without a backing DB
            using var syntheticReader = new SyntheticDataTableReader(securedSql);
            await rowWriter(syntheticReader, ct).ConfigureAwait(false);
            return securedSql;
        }

        // O10 / H-3: Process-wide read concurrency gating per user and table
        var maxConcurrentReads = _options.Value.DataSources?.MaxConcurrentReadsPerUserAndTable ?? 0;
        List<IDisposable>? leases = null;
        if (_concurrencyGate != null && maxConcurrentReads > 0 && rewrite.AccessedTables.Count > 0)
        {
            var userSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value
                          ?? user.FindFirst("sub")?.Value
                          ?? "anonymous";
            leases = new List<IDisposable>(rewrite.AccessedTables.Count);
            try
            {
                foreach (var table in rewrite.AccessedTables)
                {
                    var key = $"{tenantId.Value}|{userSid}|{table.ToQualifiedName()}".ToLowerInvariant();
                    var lease = _concurrencyGate.TryEnter(key, maxConcurrentReads)
                                ?? throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
                    leases.Add(lease);
                }
            }
            catch
            {
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }
                throw;
            }
        }

        DbTransaction? tx = null;
        bool txCommitted = false;
        try
        {
            // Real Database Execution
            await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);

            // SEC P-05 / SQ-01 & M-1 & R-SQL-3: Initialize session context inside a transaction
            // so PostgreSQL GUCs are transaction-scoped (isLocal = true) and cannot leak to pooled connections / PgBouncer.
            if (TryMapProviderToDialect(connOptions.Provider, out var connectionDialect))
            {
                var userSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value
                              ?? user.FindFirst("sub")?.Value;

                tx = await _sessionInitializer.InitializeSessionAsync(
                    connection,
                    connectionDialect,
                    tenantId,
                    userSid: userSid,
                    purpose: null,
                    requireTransaction: true,
                    ct: ct).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            if (tx != null)
            {
                command.Transaction = tx;
            }
            command.CommandText = securedSql;
            command.CommandTimeout = Math.Max(1, _options.Value.WebSql.ExecutionTimeoutSeconds);

            // Gateway-internal parameters (masking keys, row-filter parameters) are bound as parameters, never inlined
            foreach (var (paramName, paramVal) in rewrite.InternalParameters)
            {
                var p = command.CreateParameter();
                p.ParameterName = paramName.StartsWith('@') ? paramName : "@" + paramName;
                p.Value = paramVal ?? DBNull.Value;
                command.Parameters.Add(p);
            }

            if (request.Parameters != null)
            {
                var internalParamNames = new HashSet<string>(
                    rewrite.InternalParameters.Keys.Select(k => k.StartsWith('@') ? k : "@" + k),
                    StringComparer.OrdinalIgnoreCase);

                // "name" and "@name" denote the same parameter; bind it once.
                var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (paramName, paramVal) in request.Parameters)
                {
                    string normalizedName = paramName.StartsWith('@') ? paramName : "@" + paramName;
                    if (internalParamNames.Contains(normalizedName))
                    {
                        throw new WebSqlPolicyException($"Client parameter '{paramName}' collides with an internal security rewrite parameter.");
                    }

                    if (!boundNames.Add(normalizedName))
                    {
                        continue;
                    }

                    var p = command.CreateParameter();
                    p.ParameterName = normalizedName;
                    p.Value = WebSqlParameterValues.Normalize(paramVal) ?? DBNull.Value;
                    command.Parameters.Add(p);
                }
            }

            if (dmlContext.IsDml)
            {
                await ExecuteDmlInTransactionAsync(connection, command, tx, tenantId, user, dsName, dmlContext, securedSql, ct).ConfigureAwait(false);
                txCommitted = true;

                // DML produces no result set; hand an empty reader to the writer (same shape as before).
                using var emptyTable = new DataTable();
                using var emptyReader = emptyTable.CreateDataReader();
                await rowWriter(emptyReader, ct).ConfigureAwait(false);
                return securedSql;
            }

            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
            {
                if (rewrite.DeliveredRowLimit > 0)
                {
                    await rowWriter(new RowLimitedDataReader(reader, rewrite.DeliveredRowLimit), ct).ConfigureAwait(false);
                }
                else
                {
                    await rowWriter(reader, ct).ConfigureAwait(false);
                }
            }

            if (tx != null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                txCommitted = true;
            }
            return securedSql;
        }
        catch (Exception)
        {
            if (tx != null && !txCommitted)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback WebSQL transaction.");
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
            if (leases != null)
            {
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// DML guardrail: executes the statement inside a transaction and rolls it back when more rows than
    /// WebSql.MaxAffectedRows are affected (0 = unlimited). Fail-closed: if the provider cannot report the number of
    /// affected rows while a limit is configured, the statement is rolled back as well. Every outcome is audited.
    /// </summary>
    private async Task ExecuteDmlInTransactionAsync(
        DbConnection connection,
        DbCommand command,
        DbTransaction? existingTx,
        TenantId tenantId,
        ClaimsPrincipal user,
        string dsName,
        DmlAuditContext dmlContext,
        string securedSql,
        CancellationToken ct)
    {
        long maxAffectedRows = _options.Value.WebSql.MaxAffectedRows;
        int affectedRows;

        bool ownsTx = existingTx == null;
        DbTransaction transaction = existingTx ?? await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        command.Transaction = transaction;

        try
        {
            try
            {
                affectedRows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback WebSQL DML transaction.");
                }

                await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_FAILED", "DENY", securedSql, affectedRows: null, reason: ex.GetType().Name, synthetic: false, ct).ConfigureAwait(false);
                throw;
            }

            if (maxAffectedRows > 0 && (affectedRows < 0 || affectedRows > maxAffectedRows))
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback WebSQL DML transaction after row limit exceeded.");
                }

                await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_REJECTED", "DENY", securedSql, affectedRows, reason: "MaxAffectedRows exceeded; rolled back", synthetic: false, ct).ConfigureAwait(false);

                throw new WebSqlPolicyException(affectedRows < 0
                    ? "The number of rows affected by the DML statement could not be verified against WebSql.MaxAffectedRows. The statement was rolled back."
                    : $"The DML statement affected {affectedRows} rows, which exceeds the configured limit of {maxAffectedRows} (WebSql.MaxAffectedRows). The statement was rolled back.");
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_EXECUTED", "ALLOW", securedSql, affectedRows, reason: null, synthetic: false, ct).ConfigureAwait(false);
        }
        finally
        {
            if (ownsTx)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Writes a WEBSQL_DML_* entry into the audit chain. The SQL text is NOT stored (it may contain literal data values);
    /// only its SHA-256 hash, the statement type, the target tables and the number of affected rows are recorded.
    /// </summary>
    private async Task RecordDmlAuditAsync(
        TenantId tenantId,
        ClaimsPrincipal user,
        string dsName,
        DmlAuditContext dmlContext,
        string eventType,
        string decision,
        string sqlForHash,
        int? affectedRows,
        string? reason,
        bool synthetic,
        CancellationToken ct)
    {
        if (_auditLogRepository == null)
        {
            return;
        }

        string sqlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sqlForHash)));
        await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = eventType,
            ActorSid = ResolveUserSid(user),
            TargetTable = string.Join(",", dmlContext.Tables),
            Decision = decision,
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                statementType = dmlContext.StatementType?.ToString(),
                tables = dmlContext.Tables,
                dataSource = dsName,
                affectedRows,
                maxAffectedRows = _options.Value.WebSql.MaxAffectedRows,
                sqlSha256 = sqlHash,
                reason,
                synthetic
            })
        }, ct).ConfigureAwait(false);
    }

    public async Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var columns = new List<string>();
        IReadOnlyList<SqlResultColumn> columnDescriptions = Array.Empty<SqlResultColumn>();
        bool isTruncated = false;
        var sw = Stopwatch.StartNew();

        string securedSql = await ExecuteCoreAsync(
            request,
            user,
            tenantId,
            async (reader, token) =>
            {
                // WebSQL findings 2.3: unnamed and duplicate columns get unique names (_colN), so no value is lost.
                columnDescriptions = SqlResultColumns.Describe(reader);
                foreach (var column in columnDescriptions)
                {
                    columns.Add(column.Name);
                }

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    rows.Add(row);
                }

                isTruncated = reader is RowLimitedDataReader { HasMoreRows: true };
            },
            ct).ConfigureAwait(false);

        sw.Stop();

        return new GovernedSqlResult(
            OriginalSql: request.Sql,
            RewrittenSql: securedSql,
            Columns: columns.AsReadOnly(),
            Rows: rows.AsReadOnly(),
            RowCount: rows.Count,
            ElapsedMilliseconds: sw.ElapsedMilliseconds,
            Truncated: isTruncated,
            ColumnDescriptions: columnDescriptions);
    }

    /// <summary>
    /// SEC C-03: Resolves the requested data source against the allowlist. The default data source is always allowed,
    /// any other source must be listed in WebSql.AllowedDataSources. If WebSql.TenantDataSourceAllowlist has an entry
    /// for the tenant, the result must additionally be listed there (intersection).
    /// </summary>
    private string ResolveAllowedDataSource(string? requested, TenantId tenantId)
    {
        string resolved = ResolveGloballyAllowedDataSource(requested);
        if (!IsDataSourceAllowedForTenant(_options.Value.WebSql, tenantId, resolved))
        {
            throw new WebSqlPolicyException("The requested data source is not enabled for this tenant.");
        }

        return resolved;
    }

    /// <summary>
    /// SEC C-03: Per-tenant data source allowlist. Tenants without an entry keep the global allowlist.
    /// Keys are matched case-insensitively; if several keys match, the data source must be listed in all of them
    /// (only ever more restrictive). An empty list denies every data source for the tenant.
    /// </summary>
    internal static bool IsDataSourceAllowedForTenant(WebSqlOptions webSqlOptions, TenantId tenantId, string dataSourceName)
    {
        var tenantAllowlist = webSqlOptions.TenantDataSourceAllowlist;
        if (tenantAllowlist == null || tenantAllowlist.Count == 0 || string.IsNullOrEmpty(tenantId.Value))
        {
            return true;
        }

        foreach (var entry in tenantAllowlist)
        {
            if (!string.Equals(entry.Key, tenantId.Value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool listed = false;
            if (entry.Value != null)
            {
                foreach (var candidate in entry.Value)
                {
                    if (string.Equals(candidate, dataSourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        listed = true;
                        break;
                    }
                }
            }

            if (!listed)
            {
                return false;
            }
        }

        return true;
    }

    private string ResolveGloballyAllowedDataSource(string? requested)
    {
        if (TryResolveGloballyAllowedDataSource(_options.Value.WebSql, requested, out var resolved))
        {
            return resolved;
        }

        throw new WebSqlPolicyException("The requested data source is not enabled for WebSQL.");
    }

    /// <summary>
    /// SEC C-03: the default data source, a source listed in WebSql.AllowedDataSources or a mapped source
    /// (WebSql.DataSourceMappings); <paramref name="resolved"/> is its configured spelling.
    /// </summary>
    private static bool TryResolveGloballyAllowedDataSource(WebSqlOptions webSqlOptions, string? requested, out string resolved)
    {
        string defaultName = webSqlOptions.DefaultDataSourceName;
        if (string.IsNullOrWhiteSpace(requested) || string.Equals(requested, defaultName, StringComparison.OrdinalIgnoreCase))
        {
            resolved = defaultName;
            return true;
        }

        var allowed = webSqlOptions.AllowedDataSources;
        if (allowed != null)
        {
            foreach (var candidate in allowed)
            {
                if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = candidate;
                    return true;
                }
            }
        }

        if (webSqlOptions.DataSourceMappings != null && webSqlOptions.DataSourceMappings.ContainsKey(requested))
        {
            resolved = requested;
            return true;
        }

        resolved = string.Empty;
        return false;
    }

    /// <summary>
    /// True when WebSQL is enabled and <paramref name="dataSourceName"/> may be queried by <paramref name="tenantId"/>
    /// (globally allowed and within the tenant's allowlist), the same rule a statement on that source has to pass.
    /// </summary>
    internal static bool IsDataSourceQueryable(WebSqlOptions webSqlOptions, TenantId tenantId, string dataSourceName)
    {
        ArgumentNullException.ThrowIfNull(webSqlOptions);
        return webSqlOptions.Enabled &&
               !string.IsNullOrWhiteSpace(dataSourceName) &&
               TryResolveGloballyAllowedDataSource(webSqlOptions, dataSourceName, out var resolved) &&
               IsDataSourceAllowedForTenant(webSqlOptions, tenantId, resolved);
    }

    /// <summary>
    /// SQL-6: true when a column reference (<paramref name="tableOrAlias"/>, <paramref name="referencedColumn"/>) names
    /// <paramref name="columnName"/> unqualified or qualified with the alias, table name or full name of <paramref name="target"/>.
    /// </summary>
    private static bool ReferencesColumn(string? tableOrAlias, string referencedColumn, string columnName, TableAccessTarget target)
    {
        if (!string.Equals(referencedColumn, columnName, StringComparison.OrdinalIgnoreCase))
            return false;

        return tableOrAlias == null ||
               string.Equals(tableOrAlias, target.Alias, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tableOrAlias, target.TableName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tableOrAlias, target.FullName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves connection options for <paramref name="dataSourceName"/>, considering WebSql.DataSourceMappings.
    /// </summary>
    private DataSourceConnectionOptions? ResolveConnectionOptions(string dataSourceName)
    {
        string effectiveName = dataSourceName;
        if (_options.Value.WebSql?.DataSourceMappings != null &&
            _options.Value.WebSql.DataSourceMappings.TryGetValue(dataSourceName, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            effectiveName = mapped;
        }

        var connections = _options.Value.DataSources?.Connections;
        if (connections != null && connections.TryGetValue(effectiveName, out var connOptions))
        {
            return connOptions;
        }

        return null;
    }

    /// <summary>Row filters of different tables share one parameter set; the same name with two values is refused.</summary>
    private static void AddInternalRowFilterParameters(IReadOnlyDictionary<string, object?>? source, Dictionary<string, object?> target)
    {
        if (source == null)
        {
            return;
        }

        foreach (var (name, value) in source)
        {
            if (target.TryGetValue(name, out var existing) && !Equals(existing, value))
            {
                // Two row filters bind the same parameter name to different values: ambiguous -> fail-closed
                throw new WebSqlPolicyException("Conflicting row-level security parameters; the statement cannot be governed safely.");
            }

            target[name] = value;
        }
    }

    /// <summary>Architecture 1: the shared table access decision; only called with consent services present.</summary>
    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? _mandatoryFilters;

    private TableAccessPolicy AccessPolicy() =>
        new(_consentRepository!, _consentResolution!, _consentCache, _policyEnforcement, _rebacEvaluator, _clientIpResolver, _options.Value,
            _mandatoryFilters ?? Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance);

    /// <summary>
    /// SEC P-05: Dialect of the configured connection for <paramref name="dataSourceName"/>, or null when no connection
    /// is configured (synthetic dev/test path). Unknown providers are rejected (fail-closed).
    /// </summary>
    private DatabaseDialect? ResolveConnectionDialect(string dataSourceName)
    {
        DataSourceConnectionOptions? connOptions = ResolveConnectionOptions(dataSourceName);
        if (connOptions == null || string.IsNullOrWhiteSpace(connOptions.ConnectionString))
        {
            return null;
        }

        if (!TryMapProviderToDialect(connOptions.Provider, out var dialect))
        {
            _logger?.LogWarning("WebSQL rejected data source {DataSource}: provider is not supported by WebSQL.", dataSourceName);
            throw new WebSqlPolicyException("The data source uses a database provider that is not supported by WebSQL (supported: PostgreSQL, SQL Server, SQLite).");
        }

        return dialect;
    }

    /// <summary>
    /// SEC P-05: Maps DataSourceConnectionOptions.Provider to a dialect WebSQL can rewrite for. Returns false for
    /// unknown providers and for dialects without a dedicated WebSQL rewrite.
    /// </summary>
    internal static bool TryMapProviderToDialect(string? provider, out DatabaseDialect dialect) =>
        DataSourceProvider.TryResolveDialect(provider, out dialect) && IsWebSqlSupportedDialect(dialect);

    /// <summary>SEC P-05: Dialects with a dedicated, tested WebSQL rewrite.</summary>
    internal static bool IsWebSqlSupportedDialect(DatabaseDialect dialect) =>
        dialect is DatabaseDialect.PostgreSql or DatabaseDialect.SqlServer or DatabaseDialect.Sqlite;

    // SEC M-10: Identical message for "unknown table" and "access denied" (no catalog enumeration oracle, no policy details)
    private static WebSqlPolicyException TableDenied(TableAccessTarget target) =>
        new($"Access to table '{target.FullName}' is denied or the table is not registered in the governance catalog.");

    /// <summary>
    /// RR-L5-02: PostgreSQL semantics - unquoted identifiers fold to lower case, quoted identifiers are exact.
    /// The resulting physical name must match the catalogued name ordinally.
    /// </summary>
    internal static bool MatchesPostgreSqlCatalogName(TableAccessTarget target, TableIdentifier catalogTable)
    {
        var physicalTable = target.TableNameQuoted ? target.TableName : target.TableName.ToLowerInvariant();
        if (!string.Equals(physicalTable, catalogTable.TableName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(target.Schema))
        {
            var physicalSchema = target.SchemaQuoted ? target.Schema : target.Schema.ToLowerInvariant();
            if (!string.Equals(physicalSchema, catalogTable.Schema, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static Sid ResolveUserSid(ClaimsPrincipal user)
    {
        return user.GetUserSid() ?? new Sid("anonymous");
    }

    private System.Net.IPAddress ResolveClientIp(ClaimsPrincipal user)
    {
        // Unknown client IP must never satisfy "internal network" ABAC rules (no Loopback fallback).
        // H-4: a token "ip" claim is client-controlled at the IdP level and is never used as the client address.
        return _clientIpResolver?.ResolveClientIp() ?? System.Net.IPAddress.None;
    }

    private const string ClientParameterPlaceholderPrefix = "__param_";

    /// <summary>
    /// Replaces client parameters (@name) outside string literals and quoted identifiers with placeholder identifiers
    /// (__param_name) so the statement can be parsed. Only names that are actually supplied are replaced; the
    /// placeholder prefix itself is reserved.
    /// </summary>
    internal static string NormalizeClientParameters(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        out IReadOnlyCollection<string> replacedNames)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        replacedNames = names;
        if (parameters == null || parameters.Count == 0 || sql.IndexOf('@') < 0)
        {
            return sql;
        }

        if (sql.Contains(ClientParameterPlaceholderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new WebSqlPolicyException("The SQL statement uses a reserved identifier prefix.");
        }

        var supplied = new HashSet<string>(parameters.Keys.Select(k => k.TrimStart('@')), StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(sql.Length + 16);
        char quote = '\0';
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote)
                {
                    // Doubled quote inside a literal/identifier is an escaped quote, not the end.
                    if (i + 1 < sql.Length && sql[i + 1] == quote)
                    {
                        sb.Append(sql[++i]);
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            bool startsParameter = c == '@'
                && i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_')
                && (i == 0 || !(char.IsAsciiLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or '@'));
            if (startsParameter)
            {
                int end = i + 1;
                while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_'))
                {
                    end++;
                }

                string name = sql[(i + 1)..end];
                if (supplied.Contains(name))
                {
                    sb.Append(ClientParameterPlaceholderPrefix).Append(name);
                    names.Add(name);
                    i = end - 1;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Turns the placeholder identifiers of the rewritten statement back into bound parameters (@name).</summary>
    internal static string RestoreClientParameters(string securedSql, IReadOnlyCollection<string> names)
    {
        if (names.Count == 0)
        {
            return securedSql;
        }

        return System.Text.RegularExpressions.Regex.Replace(
            securedSql,
            "[\"`\\[]?" + ClientParameterPlaceholderPrefix + "([A-Za-z_][A-Za-z0-9_]*)[\"`\\]]?",
            m => names.Contains(m.Groups[1].Value) ? "@" + m.Groups[1].Value : m.Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Catalog identifier of a referenced table. Without a catalog part, a schema-qualified name (schema.table) is looked up
    /// in the domain of the data source the statement runs on (the catalog keys tables by their data source); it falls back
    /// to "default" when it is not catalogued there (see the caller).
    /// </summary>
    private static TableIdentifier ResolveTableIdentifier(TableAccessTarget target, string? dataSourceName)
    {
        // Only schema-qualified names use the data source's domain; unqualified names keep their "default" resolution.
        // This must precede TryParse, which maps every two-part name to the "default" domain (WebSQL findings 2.1).
        if (string.IsNullOrWhiteSpace(target.Catalog) && !string.IsNullOrWhiteSpace(target.Schema) &&
            !string.IsNullOrWhiteSpace(dataSourceName))
        {
            return new TableIdentifier(dataSourceName, target.Schema, target.TableName);
        }

        if (TableIdentifier.TryParse(target.FullName, out var parsed))
        {
            return parsed;
        }

        string domain = !string.IsNullOrWhiteSpace(target.Catalog) ? target.Catalog : "default";
        string schema = !string.IsNullOrWhiteSpace(target.Schema) ? target.Schema : "public";
        return new TableIdentifier(domain, schema, target.TableName);
    }

    private string GetMaskExpressionForRule(
        string columnName,
        TableMetadata tableMeta,
        TenantId tenantId,
        Dictionary<string, object?> internalParameters,
        Dictionary<string, string> hmacKeyParameterNames,
        out bool isEffectiveHmac)
    {
        isEffectiveHmac = false;
        if (tableMeta.ColumnMaskingRules.TryGetValue(columnName, out var rule))
        {
            var ruleType = rule.RuleType?.ToUpperInvariant() ?? "REDACT";
            if (ruleType == "NULLIFY")
            {
                return "NULL";
            }
            if (ruleType is "HMAC" or "HMAC_SHA256" or "HASH")
            {
                string keyId = !string.IsNullOrWhiteSpace(rule.HmacKeyId)
                    ? rule.HmacKeyId
                    : (_options.Value.DataMasking.HmacKeyId ?? "default");

                var expression = TryBuildKeyedHmacExpression(columnName, tableMeta.Dialect, keyId, tenantId, internalParameters, hmacKeyParameterNames);
                if (expression != null)
                {
                    isEffectiveHmac = true;
                    return expression;
                }

                // SEC H-13: No resolvable HMAC secret -> redact (fail-closed), never fall back to an unkeyed hash
                return BuildDefaultTypeSafeMask(tableMeta, columnName);
            }
            if (ruleType == "MASK_EMAIL")
            {
                return BuildEmailMaskExpression(columnName, tableMeta.Dialect);
            }
            if (ruleType == "MASK_IBAN")
            {
                return BuildIbanMaskExpression(columnName, tableMeta.Dialect);
            }
            if (!string.IsNullOrWhiteSpace(rule.Replacement))
            {
                var prefix = tableMeta.Dialect == DatabaseDialect.SqlServer ? "N" : string.Empty;
                return $"{prefix}'{tableMeta.Dialect.EscapeSqlLiteral(rule.Replacement)}'";
            }
        }
        return BuildDefaultTypeSafeMask(tableMeta, columnName);
    }

    private static string BuildDefaultTypeSafeMask(TableMetadata tableMeta, string columnName)
    {
        var col = tableMeta.Columns?.FirstOrDefault(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        if (col == null || string.IsNullOrWhiteSpace(col.DataType))
        {
            return "'***'";
        }

        var dt = col.DataType.Trim().ToLowerInvariant();
        if (dt.Contains('('))
        {
            dt = dt[..dt.IndexOf('(')].Trim();
        }

        return dt switch
        {
            "int" or "integer" or "bigint" or "smallint" or "tinyint" or "numeric" or "decimal" or "money" or "smallmoney" or "real" or "float" or "double precision" or "double" => "0",
            "bit" or "bool" or "boolean" => tableMeta.Dialect == DatabaseDialect.SqlServer ? "0" : "FALSE",
            "date" => "'1970-01-01'",
            "datetime" or "datetime2" or "smalldatetime" or "timestamp" or "timestamptz" => tableMeta.Dialect switch
            {
                DatabaseDialect.SqlServer => "'1970-01-01 00:00:00'",
                DatabaseDialect.PostgreSql => "'1970-01-01 00:00:00'::timestamp",
                _ => "'1970-01-01 00:00:00'"
            },
            "uniqueidentifier" or "uuid" => "'00000000-0000-0000-0000-000000000000'",
            _ => "'***'"
        };
    }

    private static string BuildEmailMaskExpression(string columnName, DatabaseDialect dialect)
    {
        if (dialect == DatabaseDialect.SqlServer)
        {
            var col = $"[{columnName.Replace("]", "]]")}]";
            return $"CASE WHEN CHARINDEX('@', CAST({col} AS NVARCHAR(MAX))) > 1 THEN SUBSTRING(CAST({col} AS NVARCHAR(MAX)), 1, 1) + '***@***' ELSE '***@***' END";
        }

        var quotedCol = $"\"{columnName.Replace("\"", "\"\"")}\"";
        if (dialect == DatabaseDialect.Sqlite)
        {
            return $"CASE WHEN INSTR(CAST({quotedCol} AS TEXT), '@') > 1 THEN SUBSTR(CAST({quotedCol} AS TEXT), 1, 1) || '***@***' ELSE '***@***' END";
        }

        return $"CASE WHEN POSITION('@' IN CAST({quotedCol} AS TEXT)) > 1 THEN SUBSTR(CAST({quotedCol} AS TEXT), 1, 1) || '***@***' ELSE '***@***' END";
    }

    private static string BuildIbanMaskExpression(string columnName, DatabaseDialect dialect)
    {
        if (dialect == DatabaseDialect.SqlServer)
        {
            var col = $"[{columnName.Replace("]", "]]")}]";
            return $"CASE WHEN LEN(CAST({col} AS NVARCHAR(MAX))) >= 8 THEN SUBSTRING(CAST({col} AS NVARCHAR(MAX)), 1, 2) + '** **** **** ' + RIGHT(CAST({col} AS NVARCHAR(MAX)), 4) ELSE '****' END";
        }

        var quotedCol = $"\"{columnName.Replace("\"", "\"\"")}\"";
        if (dialect == DatabaseDialect.Sqlite)
        {
            return $"CASE WHEN LENGTH(CAST({quotedCol} AS TEXT)) >= 8 THEN SUBSTR(CAST({quotedCol} AS TEXT), 1, 2) || '** **** **** ' || SUBSTR(CAST({quotedCol} AS TEXT), -4) ELSE '****' END";
        }

        return $"CASE WHEN LENGTH(CAST({quotedCol} AS TEXT)) >= 8 THEN SUBSTR(CAST({quotedCol} AS TEXT), 1, 2) || '** **** **** ' || SUBSTR(CAST({quotedCol} AS TEXT), LENGTH(CAST({quotedCol} AS TEXT)) - 3, 4) ELSE '****' END";
    }

    /// <summary>
    /// SEC H-13: Builds a real HMAC-SHA256 expression. The key is derived per tenant and key id (HKDF over the resolved
    /// master secret) and is bound as a command parameter; it never appears in SQL text, logs or audit records.
    /// </summary>
    private string? TryBuildKeyedHmacExpression(
        string columnName,
        DatabaseDialect dialect,
        string keyId,
        TenantId tenantId,
        Dictionary<string, object?> internalParameters,
        Dictionary<string, string> hmacKeyParameterNames)
    {
        if (_options.Value.DataMasking.PreventInDbHmacKeyExposure)
        {
            _logger?.LogWarning("WebSQL in-DB HMAC key parameter binding is disabled by policy (PreventInDbHmacKeyExposure = true); column '{Column}' is redacted fail-closed.", columnName);
            return null;
        }

        var masterKey = GetMasterHmacKey();
        if (masterKey == null)
        {
            return null;
        }

        if (!hmacKeyParameterNames.TryGetValue(keyId, out var paramBase))
        {
            paramBase = $"{InternalParameterPrefix}mk{hmacKeyParameterNames.Count}";
            hmacKeyParameterNames[keyId] = paramBase;

            byte[] derivedKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKey,
                32,
                Encoding.UTF8.GetBytes("gql-websql-tenant:" + tenantId.Value),
                Encoding.UTF8.GetBytes("gql-websql-mask:" + keyId));
            string hexKey = Convert.ToHexString(derivedKey);

            if (dialect == DatabaseDialect.SqlServer)
            {
                // T-SQL has no native HMAC: precompute the inner/outer padded keys (64-byte key == SHA-256 block size)
                byte[] keyBytes = Encoding.ASCII.GetBytes(hexKey);
                var innerPad = new byte[keyBytes.Length];
                var outerPad = new byte[keyBytes.Length];
                for (int i = 0; i < keyBytes.Length; i++)
                {
                    innerPad[i] = (byte)(keyBytes[i] ^ 0x36);
                    outerPad[i] = (byte)(keyBytes[i] ^ 0x5c);
                }

                internalParameters["@" + paramBase + "_i"] = innerPad;
                internalParameters["@" + paramBase + "_o"] = outerPad;
            }
            else
            {
                internalParameters["@" + paramBase] = hexKey;
            }
        }

        return dialect switch
        {
            DatabaseDialect.PostgreSql =>
                $"ENCODE(HMAC(CAST(\"{columnName.Replace("\"", "\"\"")}\" AS TEXT), CAST(@{paramBase} AS TEXT), 'sha256'), 'hex')",
            DatabaseDialect.SqlServer =>
                $"CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', @{paramBase}_o + HASHBYTES('SHA2_256', @{paramBase}_i + CAST(CAST([{columnName.Replace("]", "]]")}] AS NVARCHAR(MAX)) AS VARBINARY(MAX)))), 2)",
            _ =>
                $"gateway_hmac_sha256(CAST(\"{columnName.Replace("\"", "\"\"")}\" AS TEXT), @{paramBase})"
        };
    }

    private byte[]? GetMasterHmacKey()
    {
        if (_masterHmacKeyResolved)
        {
            return _masterHmacKey;
        }

        _masterHmacKeyResolved = true;
        var secretRef = _options.Value.DataMasking.HmacSecretKeyVaultRef;
        if (_secretProvider == null || string.IsNullOrWhiteSpace(secretRef))
        {
            _logger?.LogWarning("WebSQL HMAC masking key is not resolvable (no secret provider or secret reference); HMAC columns are redacted.");
            return null;
        }

        try
        {
            var key = _secretProvider.GetSecretBytes(secretRef);
            _masterHmacKey = key is { Length: > 0 } ? key : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            _logger?.LogWarning("WebSQL HMAC masking secret could not be resolved; HMAC columns are redacted.");
            _masterHmacKey = null;
        }

        return _masterHmacKey;
    }

    /// <param name="DeliveredRowLimit">Rows handed to the caller; the statement reads one more as probe (0 = no probe).</param>
    /// <param name="VirtualFilters">Virtual filters applied per referenced table (audit).</param>
    private sealed record GovernedRewrite(
        string Sql,
        IReadOnlyDictionary<string, object?> InternalParameters,
        IReadOnlyList<TableIdentifier> AccessedTables,
        string DataSourceName,
        long DeliveredRowLimit = 0,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? VirtualFilters = null);

    /// <summary>
    /// Collects the DML classification during governance so that executed AND rejected DML can be audited.
    /// </summary>
    private sealed class DmlAuditContext
    {
        public SqlStatementType? StatementType { get; set; }

        public IReadOnlyList<string> Tables { get; set; } = [];

        public bool IsDml => StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete;
    }

    /// <summary>
    /// Lightweight synthetic reader for developer / unit test environments where no physical DB is attached.
    /// </summary>
    private sealed class SyntheticDataTableReader : DbDataReader
    {
        private readonly string[] _columns = { "status", "query", "governed" };
        private readonly object?[] _values;
        private bool _readDone;

        public SyntheticDataTableReader(string rewrittenSql)
        {
            _values = new object?[] { "ok", rewrittenSql, true };
        }

        public override int FieldCount => _columns.Length;
        public override bool HasRows => true;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override int Depth => 0;

        public override bool Read()
        {
            if (!_readDone)
            {
                _readDone = true;
                return true;
            }
            return false;
        }

        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override string GetName(int ordinal) => _columns[ordinal];
        public override int GetOrdinal(string name) => Array.FindIndex(_columns, c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        public override object GetValue(int ordinal) => _values[ordinal] ?? DBNull.Value;
        public override int GetValues(object[] values)
        {
            int count = Math.Min(values.Length, _values.Length);
            for (int i = 0; i < count; i++) values[i] = _values[i] ?? DBNull.Value;
            return count;
        }
        public override bool IsDBNull(int ordinal) => _values[ordinal] == null;
        public override Type GetFieldType(int ordinal) => typeof(string);
        public override string GetDataTypeName(int ordinal) => "varchar";
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool NextResult() => false;
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(_values[ordinal]);
        public override byte GetByte(int ordinal) => Convert.ToByte(_values[ordinal]);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => Convert.ToChar(_values[ordinal]!);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(_values[ordinal]);
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(_values[ordinal]);
        public override double GetDouble(int ordinal) => Convert.ToDouble(_values[ordinal]);
        public override float GetFloat(int ordinal) => Convert.ToSingle(_values[ordinal]);
        public override Guid GetGuid(int ordinal) => Guid.Parse(_values[ordinal]!.ToString()!);
        public override short GetInt16(int ordinal) => Convert.ToInt16(_values[ordinal]);
        public override int GetInt32(int ordinal) => Convert.ToInt32(_values[ordinal]);
        public override long GetInt64(int ordinal) => Convert.ToInt64(_values[ordinal]);
        public override string GetString(int ordinal) => _values[ordinal]?.ToString() ?? string.Empty;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
    }
}
