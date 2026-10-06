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
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
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
    /// SEC P-05 / SQ-01: Second line of defense against backslash-escape lexer differentials on PostgreSQL sessions.
    /// </summary>
    internal const string PostgreSqlSessionInitializationSql = "SET standard_conforming_strings = on";

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

    private readonly FastSqlEngine _sqlEngine = new();
    private readonly IOptions<GatewayOptions> _options;
    private readonly IPolicyEnforcementService? _policyEnforcement;
    private readonly IConsentResolutionService? _consentResolution;
    private readonly ITableMetadataRepository? _tableRepository;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly ISqlConnectionFactory? _connectionFactory;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<GovernedSqlExecutionService>? _logger;
    private readonly IConsentRepository? _consentRepository;
    private readonly IKeyVaultSecretProvider? _secretProvider;

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
        IKeyVaultSecretProvider? secretProvider = null)
    {
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
    }

    public async Task<string> RewriteSqlAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        var rewrite = await RewriteCoreAsync(rawSql, user, tenantId, _options.Value.WebSql.DefaultDataSourceName, dmlContext: null, ct).ConfigureAwait(false);
        return rewrite.Sql;
    }

    private async Task<GovernedRewrite> RewriteCoreAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        string dataSourceName,
        DmlAuditContext? dmlContext,
        CancellationToken ct)
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
            return new GovernedRewrite(rawSql, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
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
        var purpose = user.FindFirst("purpose")?.Value ?? user.FindFirst("purpose_id")?.Value;

        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in user.Claims)
        {
            attributes[claim.Type] = claim.Value;
        }

        // SEC M-20: Make the requested action visible to ABAC sub-rules (set after claims so it cannot be spoofed).
        attributes["gql.action"] = isDml ? "write" : "read";

        // 5. Resolve RLS filters and Column Masking for all referenced physical tables
        var tableRlsFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tablesWithoutRls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableMaskingExpressions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var tableColumnsMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var tablesWithConsentRowFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tablesWithMaskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var internalParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var hmacKeyParameterNames = new Dictionary<string, string>(StringComparer.Ordinal);

        // SEC P-05: The SQL dialect comes from the data source configuration (the provider the connection factory will
        // actually use). Without a configured connection (synthetic dev/test path) the catalog dialect of the first table
        // is used. Every referenced table must have exactly this dialect.
        DatabaseDialect? targetDatabaseDialect = ResolveConnectionDialect(dataSourceName);

        foreach (var target in metadata.ReferencedTables)
        {
            // SEC C-03: Tables without catalog metadata (or without column metadata) cannot be governed -> reject.
            TableIdentifier tableId = ResolveTableIdentifier(target);
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
                !string.Equals(tableMeta.Table.SourceName, dataSourceName, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: catalog source does not match the requested data source.", target.FullName);
                throw TableDenied(target);
            }

            // SQ-09: If a 3-part name was used, validate the catalog part against the data source / catalog
            if (!string.IsNullOrWhiteSpace(target.Catalog) &&
                !string.Equals(target.Catalog, dataSourceName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(tableMeta.Table.SourceName) || !string.Equals(target.Catalog, tableMeta.Table.SourceName, StringComparison.OrdinalIgnoreCase)))
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: 3-part catalog '{Catalog}' does not match data source '{DataSource}'.", target.FullName, target.Catalog, dataSourceName);
                throw TableDenied(target);
            }

            // RR-L5-02: PostgreSQL identifiers are case-sensitive when quoted and fold to lower case when unquoted.
            // The physical relation addressed by the statement must be exactly the catalogued one.
            if (targetDatabaseDialect == DatabaseDialect.PostgreSql && !MatchesPostgreSqlCatalogName(target, tableMeta.Identifier))
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: PostgreSQL identifier case does not match the catalog entry.", target.FullName);
                throw TableDenied(target);
            }

            var colList = tableMeta.Columns.Select(c => c.ColumnName).ToList();
            tableColumnsMap[target.FullName] = colList;
            if (string.Equals(target.FullName, target.TableName, StringComparison.OrdinalIgnoreCase))
            {
                tableColumnsMap[target.TableName] = colList;
            }

            // SEC C-03: Consent model (same truth table as the GraphQL path)
            TableAccessDecision decision;
            if (consentBypassed)
            {
                decision = TableAccessDecision.Allowed(resolvedId, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
            }
            else if (_consentRepository == null || _consentResolution == null)
            {
                _logger?.LogError("WebSQL cannot evaluate consents (consent services not available); denying access (fail-closed).");
                throw TableDenied(target);
            }
            else
            {
                var allSubjects = groupSids.Append(userSid).ToList();
                var activeConsents = await _consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, resolvedId, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false);
                var tenantConsents = activeConsents.Where(c => c.TenantId == tenantId).ToList();
                decision = _consentResolution.ResolveAccess(userSid, groupSids, roles, resolvedId, tenantConsents, tableMeta.Dialect);
            }

            if (!decision.IsAllowed)
            {
                _logger?.LogWarning("WebSQL access to table {Table} denied by consent model.", target.FullName);
                throw TableDenied(target);
            }

            // ABAC (Casbin) is applied as an additional restriction only
            if (_policyEnforcement != null && _policyEnforcement.HasPolicies(tenantId))
            {
                var secContext = new SecurityEvaluationContext(
                    UserSid: userSid,
                    GroupSids: groupSids,
                    Tenant: tenantId,
                    TargetTable: resolvedId,
                    RequestedColumns: colList,
                    ClientIp: clientIp,
                    Timestamp: DateTimeOffset.UtcNow,
                    PurposeId: purpose,
                    Attributes: attributes);

                var policyDecision = await _policyEnforcement.EvaluatePolicyAsync(secContext, ct).ConfigureAwait(false);
                if (!policyDecision.IsAllowed)
                {
                    _logger?.LogWarning("WebSQL access to table {Table} denied by ABAC policy.", target.FullName);
                    throw TableDenied(target);
                }

                decision = RestrictWithPolicy(decision, policyDecision, tableMeta);
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

                rlsParts.Add($"{tenantColumn} = '{tenantId.Value.Replace("'", "''")}'");
            }

            if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
            {
                SqlSecurityValidator.ValidatePredicateSql(decision.CombinedRowFilterSql, "CombinedRowFilterSql");
                rlsParts.Add($"({decision.CombinedRowFilterSql})");
                AddInternalRowFilterParameters(decision.RowFilterParameters, internalParameters);
                tablesWithConsentRowFilter.Add(target.FullName);
                tablesWithConsentRowFilter.Add(target.TableName);
            }

            if (rlsParts.Count > 0)
            {
                var rlsFilter = string.Join(" AND ", rlsParts);
                tableRlsFilters[target.FullName] = rlsFilter;
                if (string.Equals(target.FullName, target.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    tableRlsFilters[target.TableName] = rlsFilter;
                }
            }
            else
            {
                tablesWithoutRls.Add(target.FullName);
                if (string.Equals(target.FullName, target.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    tablesWithoutRls.Add(target.TableName);
                }
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

                // SEC-JOIN-01: Zero-Trust Guardrail: Check if any statically redacted column is used as a JOIN predicate
                if (lvl != ColumnAccessLevel.Clear && !isEffectiveHmac &&
                    metadata.JoinConditionColumns != null && metadata.JoinConditionColumns.Contains(col.ColumnName))
                {
                    string ruleDesc = tableMeta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var mRule)
                        ? mRule.RuleType ?? "REDACT"
                        : (lvl == ColumnAccessLevel.Deny ? "DENY" : "ABAC_MASK");

                    throw new WebSqlPolicyException(
                        $"Security Policy Violation: Column '{col.ColumnName}' in table '{target.FullName}' is protected by static redaction ('{ruleDesc}') and cannot be used in a relational JOIN predicate. Joining on static constants produces false Cartesian cross-products and enables side-channel join inference attacks. Configure deterministic HMAC pseudonymization (RuleType = 'HMAC') or join on surrogate foreign keys (e.g. ID).");
                }
            }

            if (columnMasks.Count > 0)
            {
                tableMaskingExpressions[target.FullName] = columnMasks;
                if (string.Equals(target.FullName, target.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    tableMaskingExpressions[target.TableName] = columnMasks;
                }
                tablesWithMaskedColumns.Add(target.FullName);
                tablesWithMaskedColumns.Add(target.TableName);
            }
        }

        // 6. Construct RlsOptions
        long maxRows = webSqlOptions.DefaultMaxRows > 0 ? webSqlOptions.DefaultMaxRows : 1000;
        if (webSqlOptions.MaxAllowedRows > 0 && maxRows > webSqlOptions.MaxAllowedRows)
        {
            maxRows = webSqlOptions.MaxAllowedRows;
        }

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
            EnforcedMaxRows = maxRows,
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
            ColumnMaskingProvider = maskingProvider
        };

        // 7. Rewrite SQL AST
        string securedSql;
        try
        {
            securedSql = _sqlEngine.RewriteRls(rawSql.AsMemory(), rlsOptions, ct);
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

        return new GovernedRewrite(securedSql, internalParameters);
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
        string dsName = ResolveAllowedDataSource(request.DataSourceName, tenantId);

        // SEC H-13: Client parameters must not collide with gateway-internal parameters
        if (request.Parameters != null)
        {
            foreach (var paramName in request.Parameters.Keys)
            {
                if (paramName.TrimStart('@').StartsWith(InternalParameterPrefix, StringComparison.OrdinalIgnoreCase))
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
            rewrite = await RewriteCoreAsync(sqlForRewrite, user, tenantId, dsName, dmlContext, ct).ConfigureAwait(false);
        }
        catch (SecurityException policyEx) when (dmlContext.IsDml)
        {
            // Rejected DML statements are recorded in the audit chain as well.
            await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_REJECTED", "DENY", request.Sql, affectedRows: null, reason: policyEx is WebSqlPolicyException ? policyEx.Message : "policy violation", synthetic: false, ct).ConfigureAwait(false);
            throw;
        }

        string securedSql = RestoreClientParameters(rewrite.Sql, clientParameterNames);

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
                    dataSource = dsName
                })
            }, ct).ConfigureAwait(false);
        }

        // Connection resolution
        var connections = _options.Value.DataSources?.Connections;
        DataSourceConnectionOptions? connOptions = null;
        connections?.TryGetValue(dsName, out connOptions);

        if (connOptions == null || string.IsNullOrWhiteSpace(connOptions.ConnectionString) || _connectionFactory == null)
        {
            bool isDevOrTest = _environment == null ||
                               string.Equals(_environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
            bool isExplicitlyAllowed = _options.Value.AreExternalSystemsMockedIfUnreachable;

            if (!isDevOrTest && !isExplicitlyAllowed)
            {
                throw new InvalidOperationException($"No active database connection configured for data source '{dsName}'. Synthetic fallback is disabled in production.");
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

        // Real Database Execution
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);

        // SEC P-05 / SQ-01: PostgreSQL sessions are forced to standard-conforming strings (backslash is a literal character),
        // matching the gateway lexer even if the server or role default was changed.
        string? sessionInitializationSql = TryMapProviderToDialect(connOptions.Provider, out var connectionDialect)
            ? GetSessionInitializationSql(connectionDialect)
            : null;
        if (sessionInitializationSql != null)
        {
            await using var initCommand = connection.CreateCommand();
            initCommand.CommandText = sessionInitializationSql;
            initCommand.CommandTimeout = Math.Max(1, _options.Value.WebSql.ExecutionTimeoutSeconds);
            await initCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
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
            // "name" and "@name" denote the same parameter; bind it once.
            var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (paramName, paramVal) in request.Parameters)
            {
                string normalizedName = paramName.StartsWith('@') ? paramName : "@" + paramName;
                if (!boundNames.Add(normalizedName))
                {
                    continue;
                }

                var p = command.CreateParameter();
                p.ParameterName = normalizedName;
                p.Value = paramVal ?? DBNull.Value;
                command.Parameters.Add(p);
            }
        }

        if (dmlContext.IsDml)
        {
            await ExecuteDmlInTransactionAsync(connection, command, tenantId, user, dsName, dmlContext, securedSql, ct).ConfigureAwait(false);

            // DML produces no result set; hand an empty reader to the writer (same shape as before).
            using var emptyTable = new DataTable();
            using var emptyReader = emptyTable.CreateDataReader();
            await rowWriter(emptyReader, ct).ConfigureAwait(false);
            return securedSql;
        }

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        await rowWriter(reader, ct).ConfigureAwait(false);
        return securedSql;
    }

    /// <summary>
    /// DML guardrail: executes the statement inside a transaction and rolls it back when more rows than
    /// WebSql.MaxAffectedRows are affected (0 = unlimited). Fail-closed: if the provider cannot report the number of
    /// affected rows while a limit is configured, the statement is rolled back as well. Every outcome is audited.
    /// </summary>
    private async Task ExecuteDmlInTransactionAsync(
        DbConnection connection,
        DbCommand command,
        TenantId tenantId,
        ClaimsPrincipal user,
        string dsName,
        DmlAuditContext dmlContext,
        string securedSql,
        CancellationToken ct)
    {
        long maxAffectedRows = _options.Value.WebSql.MaxAffectedRows;
        int affectedRows;

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        command.Transaction = transaction;

        try
        {
            affectedRows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_FAILED", "DENY", securedSql, affectedRows: null, reason: ex.GetType().Name, synthetic: false, ct).ConfigureAwait(false);
            throw;
        }

        if (maxAffectedRows > 0 && (affectedRows < 0 || affectedRows > maxAffectedRows))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_REJECTED", "DENY", securedSql, affectedRows, reason: "MaxAffectedRows exceeded; rolled back", synthetic: false, ct).ConfigureAwait(false);

            throw new WebSqlPolicyException(affectedRows < 0
                ? "The number of rows affected by the DML statement could not be verified against WebSql.MaxAffectedRows. The statement was rolled back."
                : $"The DML statement affected {affectedRows} rows, which exceeds the configured limit of {maxAffectedRows} (WebSql.MaxAffectedRows). The statement was rolled back.");
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_EXECUTED", "ALLOW", securedSql, affectedRows, reason: null, synthetic: false, ct).ConfigureAwait(false);
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
        var sw = Stopwatch.StartNew();

        string securedSql = await ExecuteCoreAsync(
            request,
            user,
            tenantId,
            async (reader, token) =>
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(reader.GetName(i));
                }

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    rows.Add(row);
                }
            },
            ct).ConfigureAwait(false);

        sw.Stop();

        return new GovernedSqlResult(
            OriginalSql: request.Sql,
            RewrittenSql: securedSql,
            Columns: columns.AsReadOnly(),
            Rows: rows.AsReadOnly(),
            RowCount: rows.Count,
            ElapsedMilliseconds: sw.ElapsedMilliseconds);
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
        var webSqlOptions = _options.Value.WebSql;
        string defaultName = webSqlOptions.DefaultDataSourceName;
        if (string.IsNullOrWhiteSpace(requested) || string.Equals(requested, defaultName, StringComparison.OrdinalIgnoreCase))
        {
            return defaultName;
        }

        var allowed = webSqlOptions.AllowedDataSources;
        if (allowed != null)
        {
            foreach (var candidate in allowed)
            {
                if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        throw new WebSqlPolicyException("The requested data source is not enabled for WebSQL.");
    }

    /// <summary>
    /// SEC C-03: Applies an ABAC (Casbin) decision as an additional restriction on top of the consent decision.
    /// Column levels can only be lowered; an explicit Clear can only come from the consent decision.
    /// Row filters are combined with AND.
    /// </summary>
    private static TableAccessDecision RestrictWithPolicy(TableAccessDecision consentDecision, TableAccessDecision policyDecision, TableMetadata tableMeta)
    {
        var mergedColumns = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in tableMeta.Columns)
        {
            var policyLevel = policyDecision.GetColumnAccess(col.ColumnName);
            if (consentDecision.ColumnAccess.TryGetValue(col.ColumnName, out var explicitLevel))
            {
                mergedColumns[col.ColumnName] = explicitLevel < policyLevel ? explicitLevel : policyLevel;
            }
            else if (consentDecision.HasUnconstrainedColumnAllow)
            {
                if (policyLevel != ColumnAccessLevel.Clear)
                {
                    mergedColumns[col.ColumnName] = policyLevel;
                }
            }
        }

        string? mergedFilter = consentDecision.CombinedRowFilterSql;
        if (!string.IsNullOrWhiteSpace(policyDecision.CombinedRowFilterSql))
        {
            SqlSecurityValidator.ValidatePredicateSql(policyDecision.CombinedRowFilterSql, "CombinedRowFilterSql");
            mergedFilter = !string.IsNullOrWhiteSpace(mergedFilter)
                ? $"({mergedFilter}) AND ({policyDecision.CombinedRowFilterSql})"
                : policyDecision.CombinedRowFilterSql;
        }

        Dictionary<string, object?>? mergedParameters = null;
        if (consentDecision.RowFilterParameters != null || policyDecision.RowFilterParameters != null)
        {
            mergedParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            AddInternalRowFilterParameters(consentDecision.RowFilterParameters, mergedParameters);
            AddInternalRowFilterParameters(policyDecision.RowFilterParameters, mergedParameters);
        }

        return consentDecision with
        {
            ColumnAccess = mergedColumns,
            CombinedRowFilterSql = mergedFilter,
            RowFilterParameters = mergedParameters ?? consentDecision.RowFilterParameters
        };
    }

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

    /// <summary>
    /// SEC P-05: Dialect of the configured connection for <paramref name="dataSourceName"/>, or null when no connection
    /// is configured (synthetic dev/test path). Unknown providers are rejected (fail-closed).
    /// </summary>
    private DatabaseDialect? ResolveConnectionDialect(string dataSourceName)
    {
        var connections = _options.Value.DataSources?.Connections;
        DataSourceConnectionOptions? connOptions = null;
        connections?.TryGetValue(dataSourceName, out connOptions);
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
    /// SEC P-05: Maps DataSourceConnectionOptions.Provider to a dialect, using the same provider names as the SQL
    /// connection factory. Returns false for unknown providers.
    /// </summary>
    internal static bool TryMapProviderToDialect(string? provider, out DatabaseDialect dialect)
    {
        switch (provider?.Trim().ToLowerInvariant() ?? "sqlite")
        {
            case "sqlite":
            case "sqlite3":
                dialect = DatabaseDialect.Sqlite;
                return true;
            case "sqlserver":
            case "mssql":
            case "microsoft sql server":
                dialect = DatabaseDialect.SqlServer;
                return true;
            case "postgres":
            case "postgresql":
            case "npgsql":
                dialect = DatabaseDialect.PostgreSql;
                return true;
            default:
                dialect = default;
                return false;
        }
    }

    /// <summary>SEC P-05: Dialects with a dedicated, tested WebSQL rewrite.</summary>
    internal static bool IsWebSqlSupportedDialect(DatabaseDialect dialect) =>
        dialect is DatabaseDialect.PostgreSql or DatabaseDialect.SqlServer or DatabaseDialect.Sqlite;

    /// <summary>SEC P-05 / SQ-01: Session initialization statement executed before every WebSQL statement (null = none).</summary>
    internal static string? GetSessionInitializationSql(DatabaseDialect dialect) =>
        dialect == DatabaseDialect.PostgreSql ? PostgreSqlSessionInitializationSql : null;

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

    private static TableIdentifier ResolveTableIdentifier(TableAccessTarget target)
    {
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
                return "'***'";
            }
            if (!string.IsNullOrWhiteSpace(rule.Replacement))
            {
                return $"'{rule.Replacement.Replace("'", "''")}'";
            }
        }
        return "'***'";
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

    private sealed record GovernedRewrite(string Sql, IReadOnlyDictionary<string, object?> InternalParameters);

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
