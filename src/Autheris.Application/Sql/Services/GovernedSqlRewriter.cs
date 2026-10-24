namespace Autheris.Application.Sql.Services;

using Autheris.Application.Common;
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
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
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
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// AR-05 / Security invariant 1: Modularized SQL Rewriter executing the 5 strictly ordered pipeline stages.
/// Stage 1: AnalyseAndStatementPolicy (AST parse, statement type, function security)
/// Stage 2: IdentityResolution (User, Tenant, Roles, DataSource validation)
/// Stage 3: GovernanceResolution (ReBAC, TableAccessPolicy, RLS filters, column masking)
/// Stage 4: RewriteOptions (typed GovernancePolicy via GovernancePolicyFactory, CompileRequest, dialect, function allowlists)
/// Stage 5: SecureRewrite (ISqlEngine.Compile: every value bound, value-free template cache inside the engine)
/// </summary>
public sealed class GovernedSqlRewriter : ISqlRewritePipeline
{
    private static readonly SqlTokenSecurityOptions AnalysisTokenOptions = new()
    {
        RejectComments = true, RejectBackslashInStrings = true, RejectEscapedStringLiterals = true,
        RejectNonAsciiIdentifiers = true, RejectDotsInQuotedIdentifiers = true, RejectTimeTravelQueries = true, RejectReservedIdentifiers = true
    };

    private const string InternalParameterPrefix = GovernedSqlExecutionService.InternalParameterPrefix;

    private readonly ISqlEngine _sqlEngine;
    private readonly GovernancePolicyFactory? _policyFactory;
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
    private readonly SqlDataSourceResolver _dataSourceResolver;
    private readonly TypedPolicySources? _typedPolicies;
    private readonly Autheris.Application.Sql.TimeZones.ITimeZoneConversionState? _timeZoneState;   // null = conversion unavailable (fail closed)
    private readonly Autheris.Application.Caching.BoundedCache<string, SqlQueryMetadata> _stage1Analyses = new(4_096);

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
        ISqlEngine? sqlEngine = null,
        GovernancePolicyFactory? policyFactory = null,
        ISqlSecurityValidator? sqlSecurityValidator = null,
        IRebacEvaluator? rebacEvaluator = null,
        IConsentCacheService? consentCache = null,
        IMandatoryRowFilterResolver? mandatoryFilters = null,
        ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        IMemoryCache? memoryCache = null,
        SqlDataSourceResolver? dataSourceResolver = null,
        TypedPolicySources? typedPolicies = null,
        Autheris.Application.Sql.TimeZones.ITimeZoneConversionState? timeZoneState = null)
    {
        _timeZoneState = timeZoneState;
        _typedPolicies = typedPolicies;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _policyEnforcement = policyEnforcement; _consentResolution = consentResolution;
        _tableRepository = tableRepository; _clientIpResolver = clientIpResolver;
        _logger = logger; _consentRepository = consentRepository;
        _sqlEngine = sqlEngine ?? FastSqlEngine.Default; _policyFactory = policyFactory;
        _sqlSecurityValidator = sqlSecurityValidator ?? new DefaultSqlSecurityValidator();
        _rebacEvaluator = rebacEvaluator; _consentCache = consentCache;
        _mandatoryFilters = mandatoryFilters; _contractManager = contractManager;
        _accessProfileRepository = accessProfileRepository; _memoryCache = memoryCache;
        _dataSourceResolver = dataSourceResolver ?? new SqlDataSourceResolver(_options, logger);
    }

    /// <summary>
    /// G-6: runs the whole governed pipeline up to and including <see cref="ISqlEngine.Compile"/> and discards the result. The caller
    /// learns whether the statement would be accepted; no SQL text leaves the pipeline (Arrow Flight <c>GetFlightInfo</c>).
    /// </summary>
    public async Task ValidateAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        await RewriteCoreAsync(rawSql, user, tenantId, dataSourceName: null, dmlContext: null, ct).ConfigureAwait(false);
    }

    /// <summary>The compiled statement of the governed pipeline (internal; the tests assert on the compiled text and its binds).</summary>
    internal async Task<CompiledSql> CompileAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
        (await RewriteCoreAsync(rawSql, user, tenantId, dataSourceName: null, dmlContext: null, ct).ConfigureAwait(false)).Compiled;

    internal async Task<GovernedRewrite> RewriteCoreAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        string? dataSourceName,
        DmlAuditContext? dmlContext,
        CancellationToken ct,
        SqlRowLimit? rowLimit = null,
        bool probeExtraRow = false,
        bool plannedTableRead = false,
        ClientListShapes? clientLists = null)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
            throw new ArgumentException("SQL query cannot be empty or whitespace.", nameof(rawSql));
        }

        ArgumentNullException.ThrowIfNull(user);

        var webSqlOptions = _options.Value.WebSql;
        // WebSql:Enabled switches the client SQL endpoints; a gateway-planned table read is governed below like any other statement.
        if (!webSqlOptions.Enabled && !plannedTableRead)
        {
            throw new WebSqlPolicyException("WebSQL execution is disabled by gateway configuration.");
        }

        if (rawSql.Length > webSqlOptions.MaxQueryLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rawSql),
                $"SQL query length ({rawSql.Length}) exceeds the maximum allowed limit of {webSqlOptions.MaxQueryLength} characters.");
        }

        // SEC-RF107-07(e): "__rls_ks" is the reserved prefix of the key-set subquery parameters.
        if (rawSql.Contains(InternalParameterPrefix, StringComparison.OrdinalIgnoreCase) ||
            rawSql.Contains("__rls_ks", StringComparison.OrdinalIgnoreCase))
        {
            throw new WebSqlPolicyException("The SQL statement references a reserved gateway identifier.");
        }

        // -------------------------------------------------------------
        // Stage 1: Analyse & Statement-Policy (SEC C-01, SEC C-01/H-14)
        // -------------------------------------------------------------
        SqlQueryMetadata metadata;
        try
        {
            // WP-PF-12 (Stage 1): the analysis is a pure function of the statement text, so a successful result is shared between
            // requests. Only the policy checks below depend on the caller and they run every time. The key is a SHA-256 of the text
            // (a collision would hand one statement's analysis to another); failures and cancellations are never cached.
            var analysisKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawSql)));
            if (!_stage1Analyses.TryGet(analysisKey, out metadata!))
            {
                metadata = _sqlEngine.Analyze(rawSql.AsMemory(), AnalysisTokenOptions, ct);
                _stage1Analyses.Set(analysisKey, metadata);
            }
        }
        catch (WebSqlPolicyException)
        {
            throw;
        }
        catch (SecurityException secEx)
        {
            _logger?.Warning(secEx, "WebSQL statement rejected by SQL analyzer.");
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

        // SEC-277-04 / SEC-277-03: answered from the analysis alone, before any table lookup, so the response never depends on whether a
        // name is an existing, a forbidden or an unknown table. A recursive WITH in DML is never permitted (read-only scope).
        if (dmlContext != null)
        {
            dmlContext.HasRecursiveCte = metadata.HasRecursiveCte;
        }

        if (metadata.HasNonRecursiveSelfReference ||
            (metadata.HasRecursiveCte && (!webSqlOptions.AllowRecursiveCte || metadata.StatementType != SqlStatementType.Select)))
        {
            if (dmlContext != null && metadata.StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete)
            {
                dmlContext.StatementType = metadata.StatementType;   // so the rejection is audited as a DML rejection
                dmlContext.Tables = metadata.ReferencedTables.Select(t => t.FullName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "recursive common table expression");
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
            // G-9: the denylist only (no allowlist yet; the dialect allowlist is applied in stage 4).
            foreach (var functionName in metadata.FunctionCalls)
            {
                if (!SqlFunctionPolicy.IsFunctionAllowed(functionName, allowedFunctions: null, additionalDeniedFunctions: null))
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
            // #320 (SEC-RG-01): a catalog the caller may not use answers like an unknown table (SQL_POLICY_DENIED), before any metadata lookup.
            if (!_dataSourceResolver.TryResolveAllowedDataSource(distinctCatalogs[0], tenantId, out effectiveDataSourceName!))
            {
                var catalogTarget = metadata.ReferencedTables.First(t => string.Equals(t.Catalog, distinctCatalogs[0], StringComparison.OrdinalIgnoreCase));
                throw SqlDataSourceResolver.TableDenied(catalogTarget);
            }
        }
        else
        {
            effectiveDataSourceName = _dataSourceResolver.ResolveAllowedDataSource(null, tenantId);
        }

        // -------------------------------------------------------------
        // Stage 3: Table, Consent, RLS & Masking Resolution
        // -------------------------------------------------------------
        // X1: stage 3 only decides. The typed decisions (typed row policy, typed masks) go to GovernancePolicyFactory in stage 4; the
        // tenant predicate is built by the compiler from the catalog's tenant column.
        var decisions = new Dictionary<TableIdentity, TableAccessDecision>();
        var decidedTableMetadata = new Dictionary<TableIdentity, TableMetadata>();
        var decidedTargets = new Dictionary<TableIdentity, TableAccessTarget>();
        var tablesWithoutRls = new HashSet<TableIdentity>();
        var accessedTables = new List<TableIdentifier>();
        var virtualFilters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var callerAttributes = new Dictionary<string, IReadOnlyList<CallerAttributeAuditEntry>>(StringComparer.OrdinalIgnoreCase);
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

            if (!SqlDialectMapper.IsExecutable(tableMeta.Dialect))
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: SQL dialect {Dialect} is not supported by WebSQL.", target.FullName, tableMeta.Dialect);
                throw new WebSqlPolicyException(SqlDialectMapper.UnsupportedDialectMessage);
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
                ? WithTypedMasks(
                    await WithVirtualFiltersAsync(
                        TableAccessDecision.Allowed(resolvedId, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true),
                        new MandatoryFilterQuery(userSid, groupSids, roles, tenantId, tableMeta),
                        ct).ConfigureAwait(false),
                    tableMeta, tenantId)
                : await AccessPolicy().DecideAsync(
                    new TableAccessQuery(userSid, tenantId, groupSids, roles, tableMeta, user.Claims, colList, RebacEnforcement.QueryPaths, clientIp, actionAttribute, Principal: user),
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

            }

            if (decision.AppliedVirtualFilters is { Count: > 0 } applied)
            {
                // One audit key per table: the resolved qualified name (the other spellings of the same table would only multiply the record).
                virtualFilters[resolvedId.ToQualifiedName()] = applied;
            }

            if (decision.CallerAttributes is { Count: > 0 } resolvedAttributes)
            {
                callerAttributes[resolvedId.ToQualifiedName()] = resolvedAttributes;
            }

            var identity = new TableIdentity(tableMeta.Table.SchemaName, tableMeta.Table.TableName);
            decisions[identity] = decision;
            decidedTableMetadata[identity] = tableMeta;
            decidedTargets.TryAdd(identity, target);
            if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
            {
                _sqlSecurityValidator.ValidateRowFilter(decision);
            }
            else
            {
                tablesWithoutRls.Add(identity);
            }

            foreach (var col in tableMeta.Columns)
            {
                var lvl = decision.GetEffectiveColumnAccess(col.ColumnName, tableMeta);
                // A masked column is joinable only when its typed mask is the keyed HMAC pseudonym (deterministic, so joins stay meaningful).
                bool isEffectiveHmac = lvl == ColumnAccessLevel.Mask &&
                    decision.ColumnMasks is ColumnMaskPolicy typedMasks &&
                    typedMasks.TryGetSpec(col.ColumnName, out var maskSpec) &&
                    maskSpec.Kind == MaskKind.Hmac;

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

                    // #322 (SEC-RG-04): same window decision as the executor guard (masked: window code; denied: filter-style denial).
                    bool isUsedInWindow = metadata.WindowColumnReferences is { Count: > 0 } windowRefs &&
                        windowRefs.Any(wc => SqlDataSourceResolver.ReferencesColumn(wc.TableOrAlias, wc.ColumnName, col.ColumnName, target));
                    if (isUsedInWindow && lvl == ColumnAccessLevel.Mask)
                    {
                        throw new MaskedAggregationException();
                    }

                    bool isUsedInFilter = false;
                    if (metadata.FilterColumnReferences != null && metadata.FilterColumnReferences.Count > 0)
                    {
                        isUsedInFilter = metadata.FilterColumnReferences.Any(fc => SqlDataSourceResolver.ReferencesColumn(fc.TableOrAlias, fc.ColumnName, col.ColumnName, target));
                    }

                    isUsedInFilter |= isUsedInWindow;
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
        }

        // -------------------------------------------------------------
        // Stage 4: RewriteOptions Setup
        // -------------------------------------------------------------
        long maxRows = (rowLimit ?? SqlRowLimit.For(webSqlOptions))
            .Effective(metadata.HasExplicitLimit ? metadata.ExplicitLimitValue : null);

        long deliveredRowLimit = probeExtraRow && !isDml && maxRows > 0 ? maxRows : 0;
        long enforcedMaxRows = deliveredRowLimit > 0 && maxRows < long.MaxValue ? maxRows + 1 : maxRows;

        if (!targetDatabaseDialect.HasValue || !SqlDialectMapper.IsExecutable(targetDatabaseDialect.Value))
        {
            throw new WebSqlPolicyException(SqlDialectMapper.UnsupportedDialectMessage);
        }
        TargetSqlDialect targetSqlDialect = SqlDialectMapper.ToTargetDialect(targetDatabaseDialect.Value);

        EnforceEffectiveListMaximum(clientLists, targetSqlDialect, webSqlOptions);

        // GAP-013 / SEC-PG-03: governed WebSQL translates the Trino date functions (date_trunc, date_add, now, year(x) .. second(x), date_part).
        IReadOnlySet<string> allowedFunctions = SqlFunctionAllowlists.WithDateTranslation(
            SqlFunctionAllowlists.Build(targetSqlDialect, webSqlOptions.AdditionalAllowedFunctions));
        if (metadata.FunctionCalls is { Count: > 0 })
        {
            // G-9: the three-argument overload; no RlsOptions bag.
            foreach (var functionName in metadata.FunctionCalls)
            {
                if (!SqlFunctionPolicy.IsFunctionAllowed(functionName, allowedFunctions, additionalDeniedFunctions: null))
                {
                    throw new WebSqlPolicyException($"Function '{functionName}' is not permitted in WebSQL.");
                }
            }
        }

        // Fail closed when the typed assembly is not wired (a DI defect, never a silent downgrade to string policies).
        var policyFactory = _policyFactory ?? throw new SqlCompileConfigurationException();

        // Issue #107 (D-WPC-1, SEC-RF107-17): IsRead comes from the same classification as isDml; the stage never runs for anything but a SELECT.
        bool isRead = metadata.StatementType == SqlStatementType.Select && !isDml;
        GovernedPolicyInput PolicyInput(bool disabled) =>
            new(effectiveDataSourceName, tenantId, decisions, tablesWithoutRls, GovernedSubqueryStrategy.Correlated)
            {
                Metadata = decidedTableMetadata,
                KeySetStatement = new RowFilterKeySetStatement(isRead, rawSql.Length, CompileRequestFactory.DefaultMaxExpansionFactor, disabled)
            };

        var assembly = await policyFactory.CreateAssemblyAsync(PolicyInput(disabled: false), ct).ConfigureAwait(false);
        var request = CreateCompileRequest(assembly.Policy, decidedTargets, targetSqlDialect, webSqlOptions, isDml, enforcedMaxRows, allowedFunctions, clientLists);

        // -------------------------------------------------------------
        // Stage 5: Compile (Security invariant 2). Engine exceptions propagate unwrapped: SqlCompilerErrorMapper maps them to
        // stable codes with fixed messages at every error surface, and no Exception.Message is copied into a new exception.
        // -------------------------------------------------------------
        CompiledSql compiled;
        try
        {
            compiled = CompileStatement(rawSql, request, mayRetry: assembly.KeySets.HasSubstitution, ct);
        }
        catch (SqlLimitExceededException limit) when (IsRetryableLimit(limit.Kind) && assembly.KeySets.HasSubstitution)
        {
            // SEC-RF107-15 / SEC-RF107-19, D-WPC-4: once, only because a site was substituted, only for the bind or expansion limit. The values of the
            // first exception (Actual, Maximum) are a function of the key-set size: only the kind is used, nothing is logged, audited or surfaced.
            assembly.KeySets.MarkSubstitutedSitesAsRetried(
                limit.Kind == SqlLimitKind.BindParameters ? KeySetFallbackReason.BindLimitRetry : KeySetFallbackReason.ExpansionLimitRetry);
            RecordRetryMetrics(assembly.KeySets, targetSqlDialect);

            // The retry starts from the original decisions and the unsubstituted predicates (never from the first assembly's provider), with the
            // stage disabled: no epoch read, cache lookup or key query, same token and deadline. Its failure is not retried.
            var retried = await policyFactory.CreateAssemblyAsync(PolicyInput(disabled: true), ct).ConfigureAwait(false);
            request = CreateCompileRequest(retried.Policy, decidedTargets, targetSqlDialect, webSqlOptions, isDml, enforcedMaxRows, allowedFunctions, clientLists);
            compiled = CompileStatement(rawSql, request, mayRetry: false, ct);
        }

        if (_options.Value.Logging.LogGeneratedSql)
        {
            _logger?.LogDebug("GovernedSqlExecutionService: Compiled secured SQL: {SecuredSql}", compiled.Sql);
            LogKeySets(assembly.KeySets);
        }

        return new GovernedRewrite(compiled, accessedTables, effectiveDataSourceName, deliveredRowLimit, virtualFilters, assembly.KeySets.Outcomes, callerAttributes);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<TargetSqlDialect, byte> ClampWarned = new();

    /// <summary>
    /// Stage 2 of the list limits (PLAN-SQL-128 A-6): the dialect is known now, so each list is checked against
    /// <c>min(configured, MaxBindParameters - 100, MaxInListItems)</c>. A configured value above that is clamped at runtime and logged once
    /// per dialect, with numbers only. Nothing is truncated: a larger list is refused.
    /// </summary>
    private void EnforceEffectiveListMaximum(ClientListShapes? clientLists, TargetSqlDialect dialect, WebSqlOptions webSqlOptions)
    {
        if (clientLists is null)
        {
            return;
        }

        int largest = clientLists.MaxCount();
        if (largest == 0)
        {
            return;
        }

        var limits = Autheris.Application.Sql.ListParameters.ListParameterLimits.From(webSqlOptions);
        int effective = Autheris.Application.Sql.ListParameters.ListParameterLimits.EffectiveMax(
            limits.ConfiguredMaxValues, TrinoSqlEngine.Ast.Capabilities.DialectCapabilityTable.Default.Get(dialect));
        if (effective < limits.ConfiguredMaxValues && ClampWarned.TryAdd(dialect, 0))
        {
            _logger?.LogWarning("WebSQL list parameter maximum {Configured} is clamped to {Effective} for dialect {Dialect}.", limits.ConfiguredMaxValues, effective, dialect);
        }

        if (largest > effective)
        {
            throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge);
        }
    }

    private CompileRequest CreateCompileRequest(
        GovernancePolicy policy,
        Dictionary<TableIdentity, TableAccessTarget> decidedTargets,
        TargetSqlDialect targetSqlDialect,
        WebSqlOptions webSqlOptions,
        bool isDml,
        long enforcedMaxRows,
        IReadOnlySet<string> allowedFunctions,
        ClientListShapes? clientLists)
    {
        // SEC-X1-05: a table that stage 3 admitted but the assembly dropped (no typed policy, catalog load failure, missing column)
        // is denied here with the same exception as every other "table not usable" cause, before the compiler could answer differently.
        foreach (var (identity, deniedTarget) in decidedTargets)
        {
            if (policy.Catalog.Resolve(identity.ToQualifiedName()) is null)
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: it is not part of the verified governance catalog.", deniedTarget.FullName);
                throw SqlDataSourceResolver.TableDenied(deniedTarget);
            }
        }

        return CompileRequestFactory.Create(policy, targetSqlDialect, webSqlOptions, isDml, enforcedMaxRows, allowedFunctions, clientLists,
            timeZoneConversionAvailable: _timeZoneState?.IsAvailable(targetSqlDialect) ?? false,
            translateDateFunctions: true);
    }

    private CompiledSql CompileStatement(string rawSql, CompileRequest request, bool mayRetry, CancellationToken ct)
    {
        try
        {
            return _sqlEngine.Compile(rawSql.AsMemory(), request, ct);
        }
        catch (Exception ex) when (ex is SecurityException or TrinoSqlEngine.Ast.Builder.AstBuildException
                                   && !(mayRetry && ex is SqlLimitExceededException limit && IsRetryableLimit(limit.Kind)))
        {
            // A retryable limit of a statement with substituted key sets is handled by the caller without logging (SEC-RF107-15 b).
            _logger?.Warning(ex, "WebSQL statement rejected by the SQL compiler.");
            throw;
        }
    }

    private static bool IsRetryableLimit(SqlLimitKind kind) => kind is SqlLimitKind.BindParameters or SqlLimitKind.PolicyExpansionFactor;

    private static void RecordRetryMetrics(RowFilterKeySetReport report, TargetSqlDialect dialect)
    {
        foreach (var o in report.Outcomes)
        {
            if (o.Reason is KeySetFallbackReason.BindLimitRetry or KeySetFallbackReason.ExpansionLimitRetry)
            {
                Autheris.Domain.Diagnostics.GatewayDiagnostics.RowFilterKeySetFallbacks.Add(
                    1,
                    new KeyValuePair<string, object?>("dialect", dialect.ToString()),
                    new KeyValuePair<string, object?>("outcome", "Fallback"),
                    new KeyValuePair<string, object?>("reason", o.Reason.ToString()));
            }
        }
    }

    /// <summary>A-14: one Debug line per site after the compiled SQL line. Counts are allowed here (SEC-RF107-09 c); never values or hashes.</summary>
    private void LogKeySets(RowFilterKeySetReport report)
    {
        foreach (var o in report.Outcomes)
        {
            _logger?.LogDebug(
                "GovernedSqlExecutionService: Row-filter key set site {Table} outcome={Outcome} reason={Reason} keys={Keys} padded={Padded} ageSeconds={Age}",
                o.Table, o.Outcome, o.Reason, o.KeyCount, o.PaddedCount, o.AgeSeconds);
        }
    }

    /// <summary>
    /// SQLA-19: a decision built locally (consent bypass, no consent services) keeps the tenant Virtual Filters of the caller, as the cross-source
    /// path and the procedure channel do: a deny stays a deny, a predicate narrows the rows. Caller attribute filters are not resolved on this
    /// path (no attribute store), so a table that needs them is denied (fail closed).
    /// </summary>
    private async Task<TableAccessDecision> WithVirtualFiltersAsync(TableAccessDecision decision, MandatoryFilterQuery query, CancellationToken ct)
    {
        var mandatory = await (_mandatoryFilters ?? NullMandatoryRowFilterResolver.Instance).ResolveAsync(query, ct).ConfigureAwait(false);
        if (mandatory.IsDenied)
        {
            return TableAccessDecision.Denied(decision.Table, mandatory.DenyReason ?? "Denied by virtual filters.");
        }

        if (mandatory.PredicateSql == null)
        {
            return decision;
        }

        if (_typedPolicies is not null && mandatory.TypedPredicate is not null && mandatory.CallerAttributes.Count == 0)
        {
            return decision.WithMandatoryPredicate(
                _typedPolicies.RowPolicies.FromPredicate(mandatory.TypedPredicate, query.Metadata.Dialect), mandatory.AppliedFilters, mandatory.AppliedBindings);
        }

        if (mandatory.CallerAttributesTextConstant || mandatory.CallerAttributes.Count > 0)
        {
            return TableAccessDecision.Denied(decision.Table, "Denied by virtual filters.");
        }

        return decision.WithMandatoryPredicate(mandatory.PredicateSql, mandatory.AppliedFilters, mandatory.AppliedBindings);
    }

    /// <summary>
    /// A decision that is built locally (consent bypass, no consent services) still needs the typed masks of its sensitive columns, or
    /// the typed assembly would deny the table (fail closed). The masks come from the same producer as for every other decision.
    /// </summary>
    private TableAccessDecision WithTypedMasks(TableAccessDecision decision, TableMetadata metadata, TenantId tenantId) =>
        _typedPolicies is null ? decision : decision with { ColumnMasks = _typedPolicies.MaskPolicies.ForDecision(decision, metadata, tenantId) };

    internal TableAccessPolicy AccessPolicy() =>
        new(_consentRepository!, _consentResolution!,
            _consentCache ?? Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            _policyEnforcement ?? throw new InvalidOperationException("IPolicyEnforcementService is not configured; refusing to evaluate table access without policy enforcement (fail-closed)."),
            _rebacEvaluator ?? Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            _clientIpResolver,
            _options.Value,
            _mandatoryFilters ?? NullMandatoryRowFilterResolver.Instance,
            _contractManager,
            _accessProfileRepository,
            _memoryCache,
            rowPolicyFactory: _typedPolicies?.RowPolicies,
            maskPolicyFactory: _typedPolicies?.MaskPolicies,
            callerAttributes: _typedPolicies?.CallerAttributes,
            authPluginHost: _typedPolicies?.AuthPlugins);

    private System.Net.IPAddress ResolveClientIp()
    {
        return _clientIpResolver?.ResolveClientIp() ?? System.Net.IPAddress.None;
    }
}
