namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #79: Zero-read static query analysis and access requirements service.
/// Parses GraphQL queries, SQL queries, declarative SQL endpoints, and stored procedures against
/// the governance catalog and consent policies without connecting to or reading from any data source.
/// </summary>
public sealed partial class QueryAnalysisService : IQueryAnalysisService
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentRepository _consentRepository;
    private readonly IDataOwnershipRepository? _dataOwnershipRepository;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly ISchemaSunsettingService? _sunsettingService;
    private readonly GatewayOptions _gatewayOptions;
    private readonly QueryAnalysisOptions _analysisOptions;
    private readonly Autheris.Domain.Interfaces.IConsentResolutionService _consentResolution;
    private readonly Microsoft.Extensions.Logging.ILogger<QueryAnalysisService>? _logger;
    private readonly Autheris.Domain.Security.IRestrictedScopePolicy _restrictedScopes;
    private readonly IGraphQlCostAnalysisEngine? _costAnalysisEngine;
    private readonly int _maxRootFields;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public QueryAnalysisService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IAuditLogRepository auditLogRepository,
        IDataOwnershipRepository? dataOwnershipRepository = null,
        IAccessProfileRepository? accessProfileRepository = null,
        ISchemaSunsettingService? sunsettingService = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        QueryAnalysisOptions? analysisOptions = null,
        Autheris.Domain.Interfaces.IConsentResolutionService? consentResolution = null,
        Microsoft.Extensions.Logging.ILogger<QueryAnalysisService>? logger = null,
        Autheris.Domain.Security.IRestrictedScopePolicy? restrictedScopes = null,
        IGraphQlCostAnalysisEngine? costAnalysisEngine = null)
    {
        // The default is the secure list (REQ-GOV-143), never "off".
        _restrictedScopes = restrictedScopes ?? Autheris.Domain.Security.RestrictedScopePolicy.Default;
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _dataOwnershipRepository = dataOwnershipRepository;
        // The default is the production resolver (stateless), never a weaker one (issue #127).
        _consentResolution = consentResolution ?? new Autheris.Application.Services.ConsentResolutionService();
        _logger = logger;
        _accessProfileRepository = accessProfileRepository;
        _sunsettingService = sunsettingService;
        _gatewayOptions = gatewayOptions?.Value ?? new GatewayOptions();
        _costAnalysisEngine = costAnalysisEngine;
        _maxRootFields = _gatewayOptions.GraphQL.MaxRootFieldsPerOperation > 0
            ? _gatewayOptions.GraphQL.MaxRootFieldsPerOperation
            : 10;
        _analysisOptions = analysisOptions ?? new QueryAnalysisOptions
        {
            MaxAllowedCost = _gatewayOptions.GraphQL.MaxAllowedComplexity > 0
                ? _gatewayOptions.GraphQL.MaxAllowedComplexity
                : 500
        };
    }

    public QueryAnalysisService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IDataOwnershipRepository? dataOwnershipRepository = null,
        IAccessProfileRepository? accessProfileRepository = null,
        ISchemaSunsettingService? sunsettingService = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        QueryAnalysisOptions? analysisOptions = null,
        Autheris.Domain.Interfaces.IConsentResolutionService? consentResolution = null,
        Microsoft.Extensions.Logging.ILogger<QueryAnalysisService>? logger = null,
        Autheris.Domain.Security.IRestrictedScopePolicy? restrictedScopes = null,
        IGraphQlCostAnalysisEngine? costAnalysisEngine = null)
    {
        // The default is the secure list (REQ-GOV-143), never "off".
        _restrictedScopes = restrictedScopes ?? Autheris.Domain.Security.RestrictedScopePolicy.Default;
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _dataOwnershipRepository = dataOwnershipRepository;
        // The default is the production resolver (stateless), never a weaker one (issue #127).
        _consentResolution = consentResolution ?? new Autheris.Application.Services.ConsentResolutionService();
        _logger = logger;
        _accessProfileRepository = accessProfileRepository;
        _sunsettingService = sunsettingService;
        _gatewayOptions = gatewayOptions?.Value ?? new GatewayOptions();
        _costAnalysisEngine = costAnalysisEngine;
        _maxRootFields = _gatewayOptions.GraphQL.MaxRootFieldsPerOperation > 0
            ? _gatewayOptions.GraphQL.MaxRootFieldsPerOperation
            : 10;
        _analysisOptions = analysisOptions ?? new QueryAnalysisOptions
        {
            MaxAllowedCost = _gatewayOptions.GraphQL.MaxAllowedComplexity > 0
                ? _gatewayOptions.GraphQL.MaxAllowedComplexity
                : 500
        };
    }

    public async Task<QueryAnalysisResult> AnalyzeQueriesAsync(
        IReadOnlyList<QueryAnalysisItem> queries,
        ClaimsPrincipal callerPrincipal,
        ClaimsPrincipal? targetPrincipal = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(callerPrincipal);

        var callerSid = ResolvePrincipalSid(callerPrincipal);
        if (callerSid == null)
        {
            throw new UnauthorizedAccessException("Authenticated principal is required to analyze queries.");
        }

        var effectivePrincipal = targetPrincipal ?? callerPrincipal;
        var effectiveSid = ResolvePrincipalSid(effectivePrincipal) ?? callerSid.Value;

        if (targetPrincipal != null &&
            !string.Equals(callerSid.Value.Value, effectiveSid.Value, StringComparison.OrdinalIgnoreCase) &&
            !HasSimulatePrivilege(callerPrincipal))
        {
            throw new UnauthorizedAccessException(
                "Evaluating queries on behalf of another principal requires the 'governance:simulate' or 'GovernanceAdmin' role.");
        }

        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var tableLookup = BuildTableLookup(allTables);
        var activeConsents = await LoadActiveConsentsAsync(effectivePrincipal, effectiveSid.Value, ct).ConfigureAwait(false);
        var sunsetRules = await LoadSunsettingRulesAsync(ct).ConfigureAwait(false);
        var isAdmin = IsAdminPrincipal(effectivePrincipal);

        // Issue #127: the caller's own token scopes (claims overload, as the data path) are only known for the caller itself;
        // an impersonated target carries no token, so its levels are predicted for the worst case (see ResolveTableColumnLevels).
        var targetImpersonated = targetPrincipal != null &&
            !string.Equals(callerSid.Value.Value, effectiveSid.Value, StringComparison.OrdinalIgnoreCase);
        var scopeContext = new AnalysisScopeContext(
            targetImpersonated ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : TokenAccessScope.GetScopeSet(callerPrincipal.Claims),
            targetImpersonated);

        var diagnostics = new List<QueryValidationDiagnostic>();
        var outputColumns = new List<AnalyzedOutputColumn>();
        var outputColumnKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deprecatedFields = new List<DeprecatedFieldReference>();
        var deprecatedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingConsents = new List<RequestableConsentItem>();
        var missingConsentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var populatedReferencedTables = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        var hasRowFilters = false;
        var hasMandatoryVirtualFilters = false;
        var isBlockedByMandatoryFilter = false;
        var hiddenMissingDatasetsCount = 0;
        var totalEstimatedCost = 0;
        var aggregatedRootFieldCosts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < queries.Count; i++)
        {
            var item = queries[i];
            var queryName = string.IsNullOrWhiteSpace(item.Name) ? $"query_{i + 1}" : item.Name!;

            if (scopeContext.TargetImpersonated)
            {
                diagnostics.Add(new QueryValidationDiagnostic(
                    QueryDiagnosticCodes.TargetScopesUnknown,
                    "The token scopes of the target principal are unknown; the predicted column access assumes the strictest outcome with and without scope-specific consent rules.",
                    QueryName: queryName));
            }

            if (!string.IsNullOrWhiteSpace(item.GraphQlQuery))
            {
                var gqlOutcome = await AnalyzeGraphQlItemAsync(
                    item.GraphQlQuery!,
                    item.Variables,
                    queryName,
                    effectivePrincipal,
                    effectiveSid.Value,
                    isAdmin,
                    scopeContext,
                    activeConsents,
                    sunsetRules,
                    tableLookup,
                    ct).ConfigureAwait(false);

                MergeOutcome(
                    gqlOutcome,
                    diagnostics,
                    outputColumns,
                    outputColumnKeys,
                    deprecatedFields,
                    deprecatedKeys,
                    missingConsents,
                    missingConsentKeys,
                    aggregatedRootFieldCosts,
                    ref hasRowFilters,
                    ref hasMandatoryVirtualFilters,
                    ref isBlockedByMandatoryFilter,
                    ref hiddenMissingDatasetsCount,
                    ref totalEstimatedCost);
            }
            else if (item.SqlEndpoint != null)
            {
                var endpoint = item.SqlEndpoint;
                var sqlOutcome = await AnalyzeSqlTextAsync(
                    endpoint.RawSql,
                    queryName,
                    effectivePrincipal,
                    effectiveSid.Value,
                    isAdmin,
                    scopeContext,
                    activeConsents,
                    sunsetRules,
                    tableLookup,
                    ct).ConfigureAwait(false);

                VerifyOrPopulateDeclaredReferencedTables(
                    queryName,
                    endpoint.Name,
                    endpoint.ReferencedTables,
                    sqlOutcome.ExtractedTables,
                    diagnostics,
                    populatedReferencedTables);

                MergeOutcome(
                    sqlOutcome,
                    diagnostics,
                    outputColumns,
                    outputColumnKeys,
                    deprecatedFields,
                    deprecatedKeys,
                    missingConsents,
                    missingConsentKeys,
                    aggregatedRootFieldCosts,
                    ref hasRowFilters,
                    ref hasMandatoryVirtualFilters,
                    ref isBlockedByMandatoryFilter,
                    ref hiddenMissingDatasetsCount,
                    ref totalEstimatedCost);
            }
            else if (item.StoredProcedure != null)
            {
                var proc = item.StoredProcedure;
                if (!string.IsNullOrWhiteSpace(item.ProcedureSqlBody))
                {
                    var procOutcome = await AnalyzeSqlTextAsync(
                        item.ProcedureSqlBody!,
                        queryName,
                        effectivePrincipal,
                        effectiveSid.Value,
                        isAdmin,
                        scopeContext,
                        activeConsents,
                        sunsetRules,
                        tableLookup,
                        ct).ConfigureAwait(false);

                    VerifyOrPopulateDeclaredReferencedTables(
                        queryName,
                        proc.Name,
                        proc.ReferencedTables,
                        procOutcome.ExtractedTables,
                        diagnostics,
                        populatedReferencedTables);

                    MergeOutcome(
                        procOutcome,
                        diagnostics,
                        outputColumns,
                        outputColumnKeys,
                        deprecatedFields,
                        deprecatedKeys,
                        missingConsents,
                        missingConsentKeys,
                        aggregatedRootFieldCosts,
                        ref hasRowFilters,
                        ref hasMandatoryVirtualFilters,
                        ref isBlockedByMandatoryFilter,
                        ref hiddenMissingDatasetsCount,
                        ref totalEstimatedCost);
                }
                else
                {
                    var declaredOutcome = await AnalyzeDeclaredTablesOnlyAsync(
                        proc.ReferencedTables ?? Array.Empty<string>(),
                        proc.DeclaredOutputs ?? Array.Empty<string>(),
                        queryName,
                        effectivePrincipal,
                        effectiveSid.Value,
                        isAdmin,
                        scopeContext,
                        activeConsents,
                        sunsetRules,
                        tableLookup,
                        ct).ConfigureAwait(false);

                    MergeOutcome(
                        declaredOutcome,
                        diagnostics,
                        outputColumns,
                        outputColumnKeys,
                        deprecatedFields,
                        deprecatedKeys,
                        missingConsents,
                        missingConsentKeys,
                        aggregatedRootFieldCosts,
                        ref hasRowFilters,
                        ref hasMandatoryVirtualFilters,
                        ref isBlockedByMandatoryFilter,
                        ref hiddenMissingDatasetsCount,
                        ref totalEstimatedCost);
                }
            }
            else if (!string.IsNullOrWhiteSpace(item.SqlQuery))
            {
                var sqlOutcome = await AnalyzeSqlTextAsync(
                    item.SqlQuery!,
                    queryName,
                    effectivePrincipal,
                    effectiveSid.Value,
                    isAdmin,
                    scopeContext,
                    activeConsents,
                    sunsetRules,
                    tableLookup,
                    ct).ConfigureAwait(false);

                MergeOutcome(
                    sqlOutcome,
                    diagnostics,
                    outputColumns,
                    outputColumnKeys,
                    deprecatedFields,
                    deprecatedKeys,
                    missingConsents,
                    missingConsentKeys,
                    aggregatedRootFieldCosts,
                    ref hasRowFilters,
                    ref hasMandatoryVirtualFilters,
                    ref isBlockedByMandatoryFilter,
                    ref hiddenMissingDatasetsCount,
                    ref totalEstimatedCost);
            }
            else
            {
                diagnostics.Add(new QueryValidationDiagnostic(
                    QueryDiagnosticCodes.SyntaxError,
                    "Query analysis item must specify a GraphQL query, SQL query, SQL endpoint, or stored procedure.",
                    QueryName: queryName));
            }
        }

        var maxAllowedCost = _analysisOptions.MaxAllowedCost > 0 ? _analysisOptions.MaxAllowedCost : 500;
        if (totalEstimatedCost > maxAllowedCost)
        {
            var queryName = queries.Count == 1 ? (queries[0].Name ?? "query_1") : null;
            diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.QueryTooComplex,
                $"The query exceeds the complexity budget of {maxAllowedCost} (calculated cost: {totalEstimatedCost}).",
                QueryName: queryName));
        }

        var costEstimate = new QueryCostEstimate(
            EstimatedCost: totalEstimatedCost,
            MaxAllowedCost: maxAllowedCost,
            IsWithinCostLimit: totalEstimatedCost <= maxAllowedCost,
            RootFieldCosts: aggregatedRootFieldCosts.Count > 0 ? aggregatedRootFieldCosts : null);

        var isValid = diagnostics.All(d =>
            !string.Equals(d.Code, QueryDiagnosticCodes.SyntaxError, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(d.Code, QueryDiagnosticCodes.UnknownTable, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(d.Code, QueryDiagnosticCodes.UnknownColumn, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(d.Code, QueryDiagnosticCodes.ReferencedTablesMismatch, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(d.Code, QueryDiagnosticCodes.QueryTooComplex, StringComparison.OrdinalIgnoreCase));

        var validationReport = new QueryValidationReport(
            IsValid: isValid,
            Diagnostics: diagnostics,
            OutputColumns: outputColumns,
            HasRowFilters: hasRowFilters,
            HasMandatoryVirtualFilters: hasMandatoryVirtualFilters,
            CostEstimate: costEstimate,
            DeprecatedFields: deprecatedFields,
            PopulatedReferencedTables: populatedReferencedTables);

        var hiddenHint = hiddenMissingDatasetsCount > 0
            ? $"{hiddenMissingDatasetsCount} restricted dataset(s) referenced by this query require administrator or Data Owner discovery approval."
            : null;

        var accessRequirements = new QueryAccessRequirementsReport(
            MissingConsents: missingConsents,
            IsBlockedByMandatoryFilter: isBlockedByMandatoryFilter,
            HiddenMissingDatasetsCount: hiddenMissingDatasetsCount,
            HiddenDatasetsHint: hiddenHint);

        if (_auditLogRepository != null)
        {
            await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = callerPrincipal.GetTenantId(),
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = AuditEventTypes.QueryAnalysisEvaluated,
                ActorSid = callerSid.Value,
                TargetTable = string.Join(",", queries.Select((q, idx) => q.Name ?? $"query_{idx + 1}")),
                Query = $"analyze_queries(count={queries.Count}, target={effectiveSid.Value})",
                Decision = accessRequirements.IsFullyAuthorized && isValid ? "ALLOW" : "DENY",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    queryCount = queries.Count,
                    targetPrincipal = effectiveSid.Value,
                    isValid,
                    isFullyAuthorized = accessRequirements.IsFullyAuthorized,
                    missingConsentsCount = missingConsents.Count,
                    hiddenMissingDatasetsCount,
                    sourceDatabaseReads = 0
                })
            }, ct).ConfigureAwait(false);
        }

        return new QueryAnalysisResult(
            ValidationReport: validationReport,
            AccessRequirements: accessRequirements,
            SourceDatabaseReadsExecuted: 0);
    }

    public async Task<IReadOnlyList<DatasetGrantGapSummary>> EvaluateGroupAccessRequirementsAsync(
        IReadOnlyList<QueryAnalysisItem> queries,
        IReadOnlyList<ClaimsPrincipal> groupMembers,
        ClaimsPrincipal callerPrincipal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(groupMembers);
        ArgumentNullException.ThrowIfNull(callerPrincipal);

        if (!HasSimulatePrivilege(callerPrincipal))
        {
            throw new UnauthorizedAccessException(
                "Group access requirement evaluation requires the 'governance:simulate' or 'GovernanceAdmin' role.");
        }

        var missingCountsByDataset = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in groupMembers)
        {
            var result = await AnalyzeQueriesAsync(queries, callerPrincipal, member, ct).ConfigureAwait(false);
            var memberMissingTables = result.AccessRequirements.MissingConsents
                .Select(c => c.Dataset)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var dataset in memberMissingTables)
            {
                missingCountsByDataset.TryGetValue(dataset, out var current);
                missingCountsByDataset[dataset] = current + 1;
            }
        }

        return missingCountsByDataset
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => new DatasetGrantGapSummary(
                Dataset: kvp.Key,
                MembersMissingGrantCount: kvp.Value,
                TotalMembersEvaluated: groupMembers.Count))
            .ToList();
    }

    internal static Sid? ResolvePrincipalSid(ClaimsPrincipal principal)
    {
        var sid = principal.GetUserSid();
        if (sid != null)
        {
            return sid;
        }

        var idClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? principal.FindFirst("sub")?.Value
                   ?? principal.Identity?.Name;

        return !string.IsNullOrWhiteSpace(idClaim) ? new Sid(idClaim.Trim()) : (Sid?)null;
    }

    /// <summary>
    /// GovernanceAdmin or the canonical ClusterAdmin (same trust root as the catalog; ForwardAuth identities and tenant-prefixed
    /// roles never qualify as ClusterAdmin).
    /// </summary>
    internal static bool IsAdminPrincipal(ClaimsPrincipal principal)
    {
        var roles = principal.GetUserRoles();
        return roles.Contains("GovernanceAdmin") ||
               principal.IsInRole("GovernanceAdmin") ||
               Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
    }

    internal static bool HasSimulatePrivilege(ClaimsPrincipal principal)
    {
        if (IsAdminPrincipal(principal))
        {
            return true;
        }

        var roles = principal.GetUserRoles();
        return roles.Contains("governance:simulate") ||
               roles.Contains("governance:admin") ||
               principal.IsInRole("governance:simulate") ||
               principal.IsInRole("governance:admin") ||
               principal.Claims.Any(c =>
                   (string.Equals(c.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Type, "scope", StringComparison.OrdinalIgnoreCase)) &&
                   (c.Value.Contains("governance:simulate", StringComparison.OrdinalIgnoreCase) ||
                    c.Value.Contains("governance:admin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Value, "GovernanceAdmin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Value, "admin", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<IReadOnlyList<Consent>> LoadActiveConsentsAsync(
        ClaimsPrincipal principal,
        Sid subjectSid,
        CancellationToken ct)
    {
        var roles = principal.GetUserRoles();
        var tenantId = principal.GetTenantId();
        var subjects = principal.GetGroupSids().Append(subjectSid).Distinct().ToList();
        return await _consentRepository.GetAllActiveConsentsForSubjectsAsync(
            subjects,
            roles,
            DateTimeOffset.UtcNow,
            tenantId,
            ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FieldSunsettingRule>> LoadSunsettingRulesAsync(CancellationToken ct)
    {
        var rules = new List<FieldSunsettingRule>();
        if (_gatewayOptions.FieldSunsetting?.Rules is { Count: > 0 } configured)
        {
            rules.AddRange(configured);
        }

        if (_sunsettingService != null)
        {
            try
            {
                var serviceRules = await _sunsettingService.GetRulesAsync(ct).ConfigureAwait(false);
                if (serviceRules is { Count: > 0 })
                {
                    rules.AddRange(serviceRules);
                }
            }
            catch
            {
                // Fail-safe
            }
        }

        return rules;
    }

    private static Dictionary<string, TableMetadata> BuildTableLookup(IEnumerable<TableMetadata> tables)
    {
        var dict = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            var fullId = FormatDatasetId(table.Identifier);
            dict[fullId] = table;
            dict.TryAdd($"{table.Identifier.Schema}.{table.Identifier.TableName}", table);
            dict.TryAdd(table.Identifier.TableName, table);
            if (!string.IsNullOrWhiteSpace(table.Table.TableName))
            {
                dict.TryAdd(table.Table.TableName, table);
            }
        }

        return dict;
    }

    internal static string FormatDatasetId(TableIdentifier id) =>
        $"{id.Domain}.{id.Schema}.{id.TableName}".ToLowerInvariant();

    private static string ExtractUnqualifiedName(string tableName)
    {
        var idx = tableName.LastIndexOf('.');
        return idx >= 0 && idx < tableName.Length - 1 ? tableName[(idx + 1)..] : tableName;
    }
}
