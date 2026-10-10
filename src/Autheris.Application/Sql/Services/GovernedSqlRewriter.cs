namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Pipeline;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>
/// AR-05 / Sicherheits-Invariante 1: Modularized SQL Rewriter executing the 5 strictly ordered pipeline stages.
/// Stage 1: AnalyseAndStatementPolicy (AST parse, statement type, function security)
/// Stage 2: IdentityResolution (User, Tenant, Roles, DataSource validation)
/// Stage 3: GovernanceResolution (ReBAC, TableAccessPolicy, RLS filters, column masking)
/// Stage 4: RewriteOptions (Construct RlsOptions, dialect, function allowlists)
/// Stage 5: SecureRewrite (Compile SQL AST, plan cache fast-path)
/// </summary>
public sealed class GovernedSqlRewriter : ISqlRewritePipeline
{
    private static readonly SqlTokenSecurityOptions AnalysisTokenOptions = new()
    {
        RejectComments = true, RejectBackslashInStrings = true, RejectEscapedStringLiterals = true,
        RejectNonAsciiIdentifiers = true, RejectDotsInQuotedIdentifiers = true, RejectTimeTravelQueries = true
    };

    private const string InternalParameterPrefix = GovernedSqlExecutionService.InternalParameterPrefix;

    private readonly ISqlEngine _sqlEngine;
    private readonly ICompiledSqlQueryPlanCache? _planCache;
    private readonly ISqlSecurityValidator _sqlSecurityValidator;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IPolicyEnforcementService? _policyEnforcement;
    private readonly IConsentResolutionService? _consentResolution;
    private readonly ITableMetadataRepository? _tableRepository;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly ILogger? _logger;
    private readonly IRebacEvaluator? _rebacEvaluator;
    private readonly IConsentCacheService? _consentCache;
    private readonly IConsentRepository? _consentRepository;
    private readonly IMandatoryRowFilterResolver? _mandatoryFilters;
    private readonly ISchemaContractManager? _contractManager;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly IMemoryCache? _memoryCache;
    private readonly SqlDataMaskingProvider _maskingProvider;
    private readonly SqlDataSourceResolver _dataSourceResolver;

    public IReadOnlyList<SqlRewriteStageOrder> StageOrder =>
    [
        SqlRewriteStageOrder.AnalyseAndStatementPolicy,
        SqlRewriteStageOrder.IdentityResolution,
        SqlRewriteStageOrder.GovernanceResolution,
        SqlRewriteStageOrder.RewriteOptions,
        SqlRewriteStageOrder.SecureRewrite
    ];

    public GovernedSqlRewriter(
        IOptions<GatewayOptions> options,
        IPolicyEnforcementService? policyEnforcement = null,
        IConsentResolutionService? consentResolution = null,
        ITableMetadataRepository? tableRepository = null,
        IClientIpResolver? clientIpResolver = null,
        ILogger? logger = null,
        IConsentRepository? consentRepository = null,
        IKeyVaultSecretProvider? secretProvider = null,
        ISqlEngine? sqlEngine = null,
        ICompiledSqlQueryPlanCache? planCache = null,
        ISqlSecurityValidator? sqlSecurityValidator = null,
        IRebacEvaluator? rebacEvaluator = null,
        IConsentCacheService? consentCache = null,
        IMandatoryRowFilterResolver? mandatoryFilters = null,
        ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        IMemoryCache? memoryCache = null,
        SqlDataMaskingProvider? maskingProvider = null,
        SqlDataSourceResolver? dataSourceResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _policyEnforcement = policyEnforcement; _consentResolution = consentResolution;
        _tableRepository = tableRepository; _clientIpResolver = clientIpResolver;
        _logger = logger; _consentRepository = consentRepository;
        _sqlEngine = sqlEngine ?? FastSqlEngine.Default; _planCache = planCache;
        _sqlSecurityValidator = sqlSecurityValidator ?? new DefaultSqlSecurityValidator();
        _rebacEvaluator = rebacEvaluator; _consentCache = consentCache;
        _mandatoryFilters = mandatoryFilters; _contractManager = contractManager;
        _accessProfileRepository = accessProfileRepository; _memoryCache = memoryCache;
        _maskingProvider = maskingProvider ?? new SqlDataMaskingProvider(_options, secretProvider, logger);
        _dataSourceResolver = dataSourceResolver ?? new SqlDataSourceResolver(_options, logger);
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

    internal async Task<GovernedRewrite> RewriteCoreAsync(
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

        if (rawSql.Contains(InternalParameterPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new WebSqlPolicyException("The SQL statement references a reserved gateway identifier.");
        }

        // -------------------------------------------------------------
        // Stage 1: Analyse & Statement-Policy (SEC C-01, SEC C-01/H-14)
        // -------------------------------------------------------------
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
            throw new WebSqlPolicyException("The SQL statement exceeded the parser time budget.", timeoutEx);
        }
        catch (OperationCanceledException parseEx) when (!ct.IsCancellationRequested)
        {
            throw new ArgumentException("The SQL statement could not be parsed.", nameof(rawSql), parseEx);
        }

        if (metadata.StatementType == SqlStatementType.Ddl)
        {
            throw new WebSqlPolicyException("DDL statements (CREATE, DROP, ALTER, TRUNCATE, GRANT, REVOKE) are strictly forbidden in WebSQL.");
        }

        bool isDml = metadata.StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete;
        if (isDml && dmlContext != null)
        {
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
        }

        if (_options.Value.IsWebSqlGovernanceBypassed)
        {
            _logger?.LogWarning("[DANGER] WebSQL governance bypass is active! Query will be executed without RLS or AST masking.");
            return new GovernedRewrite(rawSql, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), Array.Empty<TableIdentifier>(), dataSourceName ?? _options.Value.WebSql.DefaultDataSourceName);
        }

        if (metadata.ReferencedTables.Count == 0)
        {
            throw new WebSqlPolicyException("WebSQL statements must reference at least one governed catalog table. Table-less statements (e.g. standalone function calls) are not permitted.");
        }

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

        // -------------------------------------------------------------
        // Stage 2: Identity Resolution & Data Source Binding
        // -------------------------------------------------------------
        bool consentBypassed = _options.Value.IsConsentBypassed;
        var userSidNullable = user.GetUserSid();
        if (userSidNullable == null && !consentBypassed)
        {
            throw new WebSqlPolicyException("An authenticated user identity is required for WebSQL.");
        }

        var userSid = userSidNullable ?? new Sid("anonymous");
        var groupSids = user.GetGroupSids();
        var roles = user.GetUserRoles();
        var clientIp = ResolveClientIp();

        var actionAttribute = new Dictionary<string, object?>
        {
            ["gql.action"] = isDml ? "write" : "read",
            ["action"] = isDml ? "write" : "read"
        };

        var distinctCatalogs = metadata.ReferencedTables
            .Where(t => !string.IsNullOrWhiteSpace(t.Catalog))
            .Select(t => t.Catalog!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctCatalogs.Count > 1)
        {
            if (_options.Value.WebSql?.CrossSource?.Enabled == true)
            {
                throw new CrossSourceRoutingException();
            }

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
            effectiveDataSourceName = _dataSourceResolver.ResolveAllowedDataSource(distinctCatalogs[0], tenantId);
        }
        else
        {
            effectiveDataSourceName = _dataSourceResolver.ResolveAllowedDataSource(null, tenantId);
        }

        // -------------------------------------------------------------
        // Stage 3: Table, Consent, RLS & Masking Resolution
        // -------------------------------------------------------------
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

        DatabaseDialect? targetDatabaseDialect = _dataSourceResolver.ResolveConnectionDialect(effectiveDataSourceName);

        var referenceSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in metadata.ReferencedTables)
        {
            if (referenceSpellings.TryGetValue(target.FullName, out var firstSpelling) &&
                !string.Equals(firstSpelling, target.FullName, StringComparison.Ordinal))
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: referenced with differing letter case ('{First}').", target.FullName, firstSpelling);
                throw SqlDataSourceResolver.TableDenied(target);
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
            if (tableNameCounts.TryGetValue(target.TableName, out var count) && count == 1) dict[target.TableName] = value;
            if (!string.IsNullOrWhiteSpace(target.Schema) && schemaTableCounts.TryGetValue($"{target.Schema}.{target.TableName}", out var sc) && sc == 1) dict[$"{target.Schema}.{target.TableName}"] = value;
        }

        void RegisterTableSet(ISet<string> set, TableAccessTarget target, TableIdentifier resolvedId)
        {
            set.Add(target.FullName);
            set.Add(resolvedId.ToQualifiedName());
            if (tableNameCounts.TryGetValue(target.TableName, out var count) && count == 1) set.Add(target.TableName);
            if (!string.IsNullOrWhiteSpace(target.Schema) && schemaTableCounts.TryGetValue($"{target.Schema}.{target.TableName}", out var sc) && sc == 1) set.Add($"{target.Schema}.{target.TableName}");
        }

        foreach (var target in metadata.ReferencedTables)
        {
            TableIdentifier tableId = SqlDataSourceResolver.ResolveTableIdentifier(target, effectiveDataSourceName);
            TableIdentifier resolvedId = tableId;
            TableMetadata? tableMeta = null;
            if (_tableRepository != null)
            {
                tableMeta = await _tableRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);

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
                        throw SqlDataSourceResolver.TableDenied(target);
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

            if (tableMeta == null || tableMeta.Columns.Count == 0 || !tableMeta.Table.IsActive)
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: no catalog metadata registered or table is inactive.", target.FullName);
                throw SqlDataSourceResolver.TableDenied(target);
            }

            if (tableMeta.Table.DataSourceType != DataSourceType.Sql)
            {
                if (_options.Value.WebSql?.CrossSource?.Enabled == true)
                {
                    throw new CrossSourceRoutingException();
                }

                _logger?.LogWarning("WebSQL pushdown rejected table {Table}: data source type {Type} is not supported in pushdown.", target.FullName, tableMeta.Table.DataSourceType);
                throw new WebSqlPolicyException($"WebSQL pushdown does not support table '{target.FullName}' with data source type '{tableMeta.Table.DataSourceType}'.");
            }

            if (!SqlDataSourceResolver.IsWebSqlSupportedDialect(tableMeta.Dialect))
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
                throw SqlDataSourceResolver.TableDenied(target);
            }

            if (string.Equals(resolvedId.Domain, "default", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tableId.Domain, "default", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(tableMeta.Table.SourceName) ||
                    !string.Equals(tableMeta.Table.SourceName, effectiveDataSourceName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("WebSQL rejected table {Table}: uncatalogued under data source '{DataSource}' and fallback 'default' does not match source.",
                        target.FullName, effectiveDataSourceName);
                    throw SqlDataSourceResolver.TableDenied(target);
                }
            }

            if (!string.IsNullOrWhiteSpace(tableMeta.Table.SourceName) &&
                !string.Equals(tableMeta.Table.SourceName, effectiveDataSourceName, StringComparison.OrdinalIgnoreCase))
            {
                if (_options.Value.WebSql?.CrossSource?.Enabled == true)
                {
                    throw new CrossSourceRoutingException();
                }

                _logger?.LogWarning("WebSQL rejected table {Table}: catalog source does not match the requested data source.", target.FullName);
                throw SqlDataSourceResolver.TableDenied(target);
            }

            if (!string.IsNullOrWhiteSpace(target.Catalog))
            {
                if (!string.Equals(target.Catalog, effectiveDataSourceName, StringComparison.OrdinalIgnoreCase))
                {
                    if (_options.Value.WebSql?.CrossSource?.Enabled == true)
                    {
                        throw new CrossSourceRoutingException();
                    }

                    _logger?.LogWarning("WebSQL rejected table {Table}: catalog '{Catalog}' does not match active data source '{DataSource}'.",
                        target.FullName, target.Catalog, effectiveDataSourceName);
                    throw SqlDataSourceResolver.TableDenied(target);
                }
            }

            if (targetDatabaseDialect == DatabaseDialect.PostgreSql && !SqlDataSourceResolver.MatchesPostgreSqlCatalogName(target, tableMeta.Identifier))
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: PostgreSQL identifier case does not match the catalog entry.", target.FullName);
                throw SqlDataSourceResolver.TableDenied(target);
            }

            var colList = tableMeta.Columns.Select(c => c.ColumnName).ToList();
            RegisterTableLookup(tableColumnsMap, target, resolvedId, colList);

            if (accessedTableSet.Add(resolvedId.ToQualifiedName()))
            {
                accessedTables.Add(resolvedId);
            }

            if (!consentBypassed && (_consentRepository == null || _consentResolution == null))
            {
                _logger?.LogError("WebSQL cannot evaluate consents (consent services not available); denying access (fail-closed).");
                throw SqlDataSourceResolver.TableDenied(target);
            }

            var decision = consentBypassed && (_consentRepository == null || _consentResolution == null)
                ? TableAccessDecision.Allowed(resolvedId, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true)
                : await AccessPolicy().DecideAsync(
                    new TableAccessQuery(userSid, tenantId, groupSids, roles, tableMeta, user.Claims, colList, RebacEnforcement.QueryPaths, clientIp, actionAttribute),
                    ct).ConfigureAwait(false);

            if (!decision.IsAllowed)
            {
                _logger?.LogWarning("WebSQL access to table {Table} denied: {Reasons}", target.FullName, string.Join("; ", decision.DeniedReasons));
                throw SqlDataSourceResolver.TableDenied(target);
            }

            if (isDml)
            {
                var canWrite = await AccessPolicy().CanWriteTableAsync(user, tenantId, tableMeta, ct: ct).ConfigureAwait(false);
                if (!canWrite)
                {
                    _logger?.LogWarning("WebSQL DML write access to table {Table} denied.", target.FullName);
                    throw SqlDataSourceResolver.TableDenied(target);
                }
            }

            var rlsParts = new List<string>(2);
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
                SqlDataSourceResolver.AddInternalRowFilterParameters(decision.RowFilterParameters, internalParameters);
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
                    columnMasks[col.ColumnName] = _maskingProvider.GetMaskExpressionForRule(col.ColumnName, tableMeta, tenantId, internalParameters, hmacKeyParameterNames, out isEffectiveHmac);
                }

                if (lvl != ColumnAccessLevel.Clear && !isEffectiveHmac)
                {
                    bool isUsedInJoin = false;
                    if (metadata.JoinColumnReferences != null && metadata.JoinColumnReferences.Count > 0)
                    {
                        isUsedInJoin = metadata.JoinColumnReferences.Any(jc => SqlDataSourceResolver.ReferencesColumn(jc.TableOrAlias, jc.ColumnName, col.ColumnName, target));
                    }

                    if (isUsedInJoin)
                    {
                        string ruleDesc = tableMeta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var mRule)
                            ? mRule.RuleType ?? "REDACT"
                            : (lvl == ColumnAccessLevel.Deny ? "DENY" : "ABAC_MASK");

                        throw new WebSqlPolicyException(
                            $"Security Policy Violation: Column '{col.ColumnName}' in table '{target.FullName}' is protected by static redaction ('{ruleDesc}') and cannot be used in a relational JOIN predicate. Joining on static constants produces false Cartesian cross-products and enables side-channel join inference attacks. Configure deterministic HMAC pseudonymization (RuleType = 'HMAC') or join on surrogate foreign keys (e.g. ID).");
                    }

                    bool isUsedInFilter = false;
                    if (metadata.FilterColumnReferences != null && metadata.FilterColumnReferences.Count > 0)
                    {
                        isUsedInFilter = metadata.FilterColumnReferences.Any(fc => SqlDataSourceResolver.ReferencesColumn(fc.TableOrAlias, fc.ColumnName, col.ColumnName, target));
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

        // -------------------------------------------------------------
        // Stage 4: RewriteOptions Setup
        // -------------------------------------------------------------
        long maxRows = (rowLimit ?? SqlRowLimit.For(webSqlOptions))
            .Effective(metadata.HasExplicitLimit ? metadata.ExplicitLimitValue : null);

        long deliveredRowLimit = probeExtraRow && !isDml && maxRows > 0 ? maxRows : 0;
        long enforcedMaxRows = deliveredRowLimit > 0 ? maxRows + 1 : maxRows;

        const string denyAllFilter = "1 = 0";

        if (!targetDatabaseDialect.HasValue || !SqlDialectMapper.IsExecutable(targetDatabaseDialect.Value))
        {
            throw new WebSqlPolicyException("WebSQL only supports tables of PostgreSQL, SQL Server and SQLite data sources.");
        }
        TargetSqlDialect targetSqlDialect = SqlDialectMapper.ToTargetDialect(targetDatabaseDialect.Value);

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

        var maskingPolicyProvider = new DefaultColumnMaskingPolicyProvider(
            hasMaskPredicate: (tbl, col) => tableMaskingExpressions.TryGetValue(tbl, out var dict) && dict.ContainsKey(col),
            maskExpressionProvider: (tbl, col) => tableMaskingExpressions.TryGetValue(tbl, out var dict) && dict.TryGetValue(col, out var expr) ? expr : "'***'")
        {
            FallbackToSimpleName = false
        };

        var rlsOptions = new RlsOptions
        {
            AppendTableAlias = true,
            EnforceCatalogProjection = true,
            EnforcedMaxRows = enforcedMaxRows,
            EnforceReadOnlyQueries = !isDml,
            TargetDialect = targetSqlDialect,
            RejectComments = true,
            RejectBackslashInStrings = true,
            RejectEscapedStringLiterals = true,
            RejectDollarQuoting = targetSqlDialect != TargetSqlDialect.PostgreSql,
            RejectBracketLexerDifferentials = targetSqlDialect is TargetSqlDialect.SqlServer or TargetSqlDialect.Sqlite,
            RejectNonAsciiIdentifiers = true,
            RejectDotsInQuotedIdentifiers = true,
            RejectTimeTravelQueries = true,
            RejectConsentFilteredInsert = true,
            RejectWholeRowReferencesInDml = true,
            TablesWithConsentRowFilter = tablesWithConsentRowFilter,
            TablesWithMaskedColumns = tablesWithMaskedColumns,
            ExpectedTenantValue = tenantId.Value,
            TenantColumnName = primaryTenantColumn ?? "tenant_id",
            TableTenantColumns = tableTenantColumns,
            RequireTenantColumnInInsert = true,
            EnforceFunctionPolicy = true,
            AllowedFunctions = allowedFunctions,
            AllowInlineFunctionDefinitions = false,
            RejectMaskedColumnsInDml = true,
            RejectUnfilteredDml = true,
            PolicyProvider = policyProvider,
            TableColumnsProvider = tbl => tableColumnsMap.TryGetValue(tbl, out var cols) ? cols : null,
            ColumnMaskingProvider = maskingPolicyProvider,
            PolicyFiltersAreTargetDialectSql = true,
            RewriterEngine = _options.Value.WebSql.SqlRewriterEngine
        };

        // -------------------------------------------------------------
        // Stage 5: Secure Rewrite & Plan Cache Lookup (Sicherheits-Invariante 2)
        // -------------------------------------------------------------
        string securedSql;
        ulong queryHash = 0;
        ulong policyHash = 0;
        var planCache = _planCache;
        bool canUsePlanCache = planCache != null && targetDatabaseDialect.HasValue;
        string policyFingerprint = $"tenant:{tenantId};dialect:{targetDatabaseDialect.GetValueOrDefault()};ds:{effectiveDataSourceName};engine:{webSqlOptions.SqlRewriterEngine}";

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
                _options.Value.RowFilters.SubqueryStrategy,
                tableColumnsMap,
                allowedFunctions);

            if (planCache.TryGetCompiledSql(rawSql, queryHash, targetDatabaseDialect.Value, tenantId, policyHash, policyFingerprint, effectiveDataSourceName, out var cachedSql) && !string.IsNullOrEmpty(cachedSql))
            {
                return new GovernedRewrite(cachedSql, internalParameters, accessedTables, effectiveDataSourceName, deliveredRowLimit, virtualFilters);
            }
        }

        try
        {
            securedSql = _sqlEngine.RewriteRls(rawSql.AsMemory(), rlsOptions, ct);
            if (canUsePlanCache && planCache != null && !string.IsNullOrEmpty(securedSql))
            {
                planCache.SetCompiledSql(rawSql, queryHash, targetDatabaseDialect.Value, tenantId, policyHash, policyFingerprint, effectiveDataSourceName, securedSql);
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

    private TableAccessPolicy AccessPolicy() =>
        new(_consentRepository!, _consentResolution!,
            _consentCache ?? Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            _policyEnforcement ?? Autheris.Application.Policy.Services.NullPolicyEnforcementService.Instance,
            _rebacEvaluator ?? Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            _clientIpResolver,
            _options.Value,
            _mandatoryFilters ?? NullMandatoryRowFilterResolver.Instance,
            _contractManager,
            _accessProfileRepository,
            _memoryCache);

    private System.Net.IPAddress ResolveClientIp()
    {
        return _clientIpResolver?.ResolveClientIp() ?? System.Net.IPAddress.None;
    }
}
