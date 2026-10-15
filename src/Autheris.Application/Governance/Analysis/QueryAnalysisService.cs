namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using HotChocolate.Language;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

/// <summary>
/// Issue #79: Zero-read static query analysis and access requirements service.
/// Parses GraphQL queries, SQL queries, declarative SQL endpoints, and stored procedures against
/// the governance catalog and consent policies without connecting to or reading from any data source.
/// </summary>
public sealed class QueryAnalysisService : IQueryAnalysisService
{
    private static readonly Regex SqlJoinOnColumnRegex = new(
        @"\bJOIN\b[\s\S]*?\bON\b\s+([\s\S]+?)(?=\b(?:WHERE|GROUP\s+BY|ORDER\s+BY|LIMIT|HAVING|JOIN|INNER|LEFT|RIGHT|FULL)\b|;|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SqlWhereClauseRegex = new(
        @"\bWHERE\b\s+([\s\S]+?)(?=\b(?:GROUP\s+BY|ORDER\s+BY|LIMIT|HAVING)\b|;|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IdentifierTokenRegex = new(
        @"\b([a-zA-Z_][a-zA-Z0-9_]*)(?:\.([a-zA-Z_][a-zA-Z0-9_]*))?\b",
        RegexOptions.Compiled);

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentRepository _consentRepository;
    private readonly IDataOwnershipRepository? _dataOwnershipRepository;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly ISchemaSunsettingService? _sunsettingService;
    private readonly GatewayOptions _gatewayOptions;
    private readonly QueryAnalysisOptions _analysisOptions;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public QueryAnalysisService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IAuditLogRepository auditLogRepository,
        IDataOwnershipRepository? dataOwnershipRepository = null,
        IAccessProfileRepository? accessProfileRepository = null,
        ISchemaSunsettingService? sunsettingService = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        QueryAnalysisOptions? analysisOptions = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _dataOwnershipRepository = dataOwnershipRepository;
        _accessProfileRepository = accessProfileRepository;
        _sunsettingService = sunsettingService;
        _gatewayOptions = gatewayOptions?.Value ?? new GatewayOptions();
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
        QueryAnalysisOptions? analysisOptions = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _dataOwnershipRepository = dataOwnershipRepository;
        _accessProfileRepository = accessProfileRepository;
        _sunsettingService = sunsettingService;
        _gatewayOptions = gatewayOptions?.Value ?? new GatewayOptions();
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

        for (var i = 0; i < queries.Count; i++)
        {
            var item = queries[i];
            var queryName = string.IsNullOrWhiteSpace(item.Name) ? $"query_{i + 1}" : item.Name!;

            if (!string.IsNullOrWhiteSpace(item.GraphQlQuery))
            {
                var gqlOutcome = await AnalyzeGraphQlItemAsync(
                    item.GraphQlQuery!,
                    item.Variables,
                    queryName,
                    effectivePrincipal,
                    effectiveSid.Value,
                    isAdmin,
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
        var costEstimate = new QueryCostEstimate(
            EstimatedCost: totalEstimatedCost,
            MaxAllowedCost: maxAllowedCost,
            IsWithinCostLimit: totalEstimatedCost <= maxAllowedCost);

        var isValid = diagnostics.All(d =>
            !string.Equals(d.Code, QueryDiagnosticCodes.SyntaxError, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(d.Code, QueryDiagnosticCodes.UnknownTable, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(d.Code, QueryDiagnosticCodes.UnknownColumn, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(d.Code, QueryDiagnosticCodes.ReferencedTablesMismatch, StringComparison.OrdinalIgnoreCase));

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

    internal static bool IsAdminPrincipal(ClaimsPrincipal principal)
    {
        var roles = principal.GetUserRoles();
        return roles.Contains("GovernanceAdmin") ||
               roles.Contains("ClusterAdmin") ||
               principal.IsInRole("GovernanceAdmin") ||
               principal.IsInRole("ClusterAdmin") ||
               principal.IsInRole("admin");
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

    private static void VerifyOrPopulateDeclaredReferencedTables(
        string queryName,
        string definitionName,
        IReadOnlyList<string>? declaredTables,
        IReadOnlyList<string> astExtractedTables,
        List<QueryValidationDiagnostic> diagnostics,
        Dictionary<string, IReadOnlyList<string>> populatedReferencedTables)
    {
        var normalizedAst = astExtractedTables
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (declaredTables == null || declaredTables.Count == 0)
        {
            populatedReferencedTables[definitionName] = normalizedAst;
            return;
        }

        var declaredSet = new HashSet<string>(
            declaredTables.Select(ExtractUnqualifiedName),
            StringComparer.OrdinalIgnoreCase);
        var astSet = new HashSet<string>(
            normalizedAst.Select(ExtractUnqualifiedName),
            StringComparer.OrdinalIgnoreCase);

        if (!declaredSet.SetEquals(astSet))
        {
            var undeclared = astSet.Except(declaredSet, StringComparer.OrdinalIgnoreCase).ToList();
            var unused = declaredSet.Except(astSet, StringComparer.OrdinalIgnoreCase).ToList();
            diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.ReferencedTablesMismatch,
                $"Declared ReferencedTables [{string.Join(", ", declaredTables)}] on '{definitionName}' do not match AST-extracted tables [{string.Join(", ", normalizedAst)}] (undeclared: [{string.Join(", ", undeclared)}], unused: [{string.Join(", ", unused)}]).",
                QueryName: queryName));
        }
    }

    private async Task<SingleQueryAnalysisOutcome> AnalyzeDeclaredTablesOnlyAsync(
        IReadOnlyList<string> declaredTables,
        IReadOnlyList<string> declaredOutputs,
        string queryName,
        ClaimsPrincipal effectivePrincipal,
        Sid effectiveSid,
        bool isAdmin,
        IReadOnlyList<Consent> activeConsents,
        IReadOnlyList<FieldSunsettingRule> sunsetRules,
        Dictionary<string, TableMetadata> tableLookup,
        CancellationToken ct)
    {
        var outcome = new SingleQueryAnalysisOutcome
        {
            EstimatedCost = Math.Max(1, declaredTables.Count) * _analysisOptions.CostPerRequestedField
        };

        foreach (var rawTable in declaredTables)
        {
            outcome.ExtractedTables.Add(rawTable);
            await EvaluateTableAndColumnsAsync(
                rawTable,
                requestedColumns: declaredOutputs,
                filterColumns: Array.Empty<string>(),
                providedFilterColumns: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                queryName,
                effectivePrincipal,
                effectiveSid,
                isAdmin,
                activeConsents,
                sunsetRules,
                tableLookup,
                selectAllColumns: declaredOutputs.Count == 0,
                outcome,
                ct).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<SingleQueryAnalysisOutcome> AnalyzeGraphQlItemAsync(
        string graphQlQuery,
        IReadOnlyDictionary<string, object?>? variables,
        string queryName,
        ClaimsPrincipal effectivePrincipal,
        Sid effectiveSid,
        bool isAdmin,
        IReadOnlyList<Consent> activeConsents,
        IReadOnlyList<FieldSunsettingRule> sunsetRules,
        Dictionary<string, TableMetadata> tableLookup,
        CancellationToken ct)
    {
        var outcome = new SingleQueryAnalysisOutcome();
        DocumentNode document;
        try
        {
            document = Utf8GraphQLParser.Parse(graphQlQuery);
        }
        catch (SyntaxException ex)
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.SyntaxError,
                $"GraphQL syntax error: {ex.Message}",
                QueryName: queryName));
            return outcome;
        }

        var operations = document.Definitions.OfType<OperationDefinitionNode>().ToList();
        if (operations.Count == 0)
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.SyntaxError,
                "GraphQL document does not contain any executable operation.",
                QueryName: queryName));
            return outcome;
        }

        var totalFields = 0;
        var maxDepth = 0;

        foreach (var op in operations)
        {
            foreach (var rootField in op.SelectionSet.Selections.OfType<FieldNode>())
            {
                var fieldName = rootField.Name.Value;
                if (fieldName.StartsWith("__", StringComparison.Ordinal))
                {
                    continue;
                }

                outcome.ExtractedTables.Add(fieldName);

                var requestedColumns = new List<string>();
                var filterColumns = new List<string>();
                var providedFilterColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                ExtractGraphQlFilterArguments(rootField.Arguments, variables, filterColumns, providedFilterColumns);
                CollectSelectedGraphQlFields(rootField.SelectionSet, requestedColumns, currentDepth: 1, ref maxDepth, ref totalFields);

                await EvaluateTableAndColumnsAsync(
                    fieldName,
                    requestedColumns,
                    filterColumns,
                    providedFilterColumns,
                    queryName,
                    effectivePrincipal,
                    effectiveSid,
                    isAdmin,
                    activeConsents,
                    sunsetRules,
                    tableLookup,
                    selectAllColumns: false,
                    outcome,
                    ct).ConfigureAwait(false);
            }
        }

        outcome.EstimatedCost = Math.Max(1, totalFields) * Math.Max(1, _analysisOptions.CostPerRequestedField)
            + Math.Max(0, maxDepth - 1) * _analysisOptions.CostPerJoin;

        return outcome;
    }

    private static void ExtractGraphQlFilterArguments(
        IReadOnlyList<ArgumentNode> arguments,
        IReadOnlyDictionary<string, object?>? variables,
        List<string> filterColumns,
        HashSet<string> providedFilterColumns)
    {
        foreach (var arg in arguments)
        {
            var argName = arg.Name.Value;
            if (string.Equals(argName, "where", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argName, "filter", StringComparison.OrdinalIgnoreCase))
            {
                ExtractFilterFieldsFromValueNode(arg.Value, variables, filterColumns, providedFilterColumns);
            }
            else
            {
                providedFilterColumns.Add(argName);
            }
        }
    }

    private static void ExtractFilterFieldsFromValueNode(
        IValueNode valueNode,
        IReadOnlyDictionary<string, object?>? variables,
        List<string> filterColumns,
        HashSet<string> providedFilterColumns)
    {
        if (valueNode is ObjectValueNode objNode)
        {
            foreach (var field in objNode.Fields)
            {
                var name = field.Name.Value;
                if (string.Equals(name, "and", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "or", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "not", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractFilterFieldsFromValueNode(field.Value, variables, filterColumns, providedFilterColumns);
                    continue;
                }

                var baseCol = StripGraphQlOperatorSuffix(name);
                filterColumns.Add(baseCol);
                providedFilterColumns.Add(baseCol);
            }
        }
        else if (valueNode is ListValueNode listNode)
        {
            foreach (var item in listNode.Items)
            {
                ExtractFilterFieldsFromValueNode(item, variables, filterColumns, providedFilterColumns);
            }
        }
        else if (valueNode is VariableNode varNode &&
                 variables != null &&
                 variables.TryGetValue(varNode.Name.Value, out var varObj) &&
                 varObj is IEnumerable<KeyValuePair<string, object?>> dict)
        {
            foreach (var kvp in dict)
            {
                var baseCol = StripGraphQlOperatorSuffix(kvp.Key);
                filterColumns.Add(baseCol);
                providedFilterColumns.Add(baseCol);
            }
        }
    }

    private static string StripGraphQlOperatorSuffix(string fieldName)
    {
        string[] suffixes = ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_in", "_nin", "_contains", "_startsWith", "_endsWith"];
        foreach (var suffix in suffixes)
        {
            if (fieldName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && fieldName.Length > suffix.Length)
            {
                return fieldName[..^suffix.Length];
            }
        }

        return fieldName;
    }

    private static void CollectSelectedGraphQlFields(
        SelectionSetNode? selectionSet,
        List<string> columns,
        int currentDepth,
        ref int maxDepth,
        ref int totalFields)
    {
        if (selectionSet == null)
        {
            return;
        }

        if (currentDepth > maxDepth)
        {
            maxDepth = currentDepth;
        }

        foreach (var selection in selectionSet.Selections)
        {
            if (selection is FieldNode field)
            {
                var name = field.Name.Value;
                if (name.StartsWith("__", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(name, "nodes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "edges", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "node", StringComparison.OrdinalIgnoreCase))
                {
                    CollectSelectedGraphQlFields(field.SelectionSet, columns, currentDepth + 1, ref maxDepth, ref totalFields);
                    continue;
                }

                totalFields++;
                if (field.SelectionSet == null || field.SelectionSet.Selections.Count == 0)
                {
                    columns.Add(name);
                }
                else
                {
                    CollectSelectedGraphQlFields(field.SelectionSet, columns, currentDepth + 1, ref maxDepth, ref totalFields);
                }
            }
        }
    }

    private async Task<SingleQueryAnalysisOutcome> AnalyzeSqlTextAsync(
        string sql,
        string queryName,
        ClaimsPrincipal effectivePrincipal,
        Sid effectiveSid,
        bool isAdmin,
        IReadOnlyList<Consent> activeConsents,
        IReadOnlyList<FieldSunsettingRule> sunsetRules,
        Dictionary<string, TableMetadata> tableLookup,
        CancellationToken ct)
    {
        var outcome = new SingleQueryAnalysisOutcome();
        var trimmed = sql.Trim();

        if (!IsBalancedSqlSyntax(trimmed))
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.SyntaxError,
                "SQL syntax error: unbalanced parentheses, quotes, or malformed statement.",
                QueryName: queryName));
            return outcome;
        }

        SqlQueryMetadata metadata;
        try
        {
            var normalizedSql = SqlParameterExtractor.NormalizeForAst(trimmed);
            var (tree, _) = FastSqlEngine.Default.Parse(normalizedSql.AsMemory());
            var analyzer = new SqlQueryAnalyzer();
            metadata = analyzer.Analyze(tree);
        }
        catch (Exception ex)
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.SyntaxError,
                $"SQL syntax error: {ex.Message}",
                QueryName: queryName));
            return outcome;
        }

        if (metadata.StatementType != SqlStatementType.Select || metadata.ReferencedTables.Count == 0)
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.SyntaxError,
                "SQL statement is not a valid SELECT query or is missing a valid FROM clause.",
                QueryName: queryName));
            return outcome;
        }

        var aliasToTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            outcome.ExtractedTables.Add(key);
            aliasToTable[key] = key;
            aliasToTable[target.TableName] = key;
            aliasToTable[ExtractUnqualifiedName(key)] = key;
            if (!string.IsNullOrWhiteSpace(target.Alias))
            {
                aliasToTable[target.Alias!] = key;
            }
        }

        var selectAll = metadata.ProjectedColumns.Count == 0 ||
                        metadata.ProjectedColumns.Any(c => c == "*" || c.EndsWith(".*", StringComparison.Ordinal));

        var columnsByTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var filterColumnsByTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            columnsByTable[key] = new List<string>();
            filterColumnsByTable[key] = new List<string>();
        }

        foreach (var rawProjected in metadata.ProjectedColumns)
        {
            var colExpr = StripSqlAlias(rawProjected);
            if (colExpr == "*" || colExpr.EndsWith(".*", StringComparison.Ordinal))
            {
                continue;
            }

            AssignColumnReferenceToTable(colExpr, metadata.ReferencedTables, aliasToTable, tableLookup, columnsByTable);
        }

        if (metadata.FilterColumnReferences != null)
        {
            foreach (var fRef in metadata.FilterColumnReferences)
            {
                var qualified = !string.IsNullOrWhiteSpace(fRef.TableOrAlias)
                    ? $"{fRef.TableOrAlias}.{fRef.ColumnName}"
                    : fRef.ColumnName;
                AssignColumnReferenceToTable(qualified, metadata.ReferencedTables, aliasToTable, tableLookup, filterColumnsByTable);
            }
        }

        if (metadata.JoinColumnReferences != null)
        {
            foreach (var jRef in metadata.JoinColumnReferences)
            {
                var qualified = !string.IsNullOrWhiteSpace(jRef.TableOrAlias)
                    ? $"{jRef.TableOrAlias}.{jRef.ColumnName}"
                    : jRef.ColumnName;
                AssignColumnReferenceToTable(qualified, metadata.ReferencedTables, aliasToTable, tableLookup, filterColumnsByTable);
            }
        }

        ExtractAdditionalSqlFilterColumns(trimmed, metadata.ReferencedTables, aliasToTable, tableLookup, filterColumnsByTable);
        var providedFilterCols = ExtractSqlEqualityFilterColumns(trimmed);

        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            var tableCols = columnsByTable[key];
            var filterCols = filterColumnsByTable[key];

            await EvaluateTableAndColumnsAsync(
                key,
                tableCols,
                filterCols,
                providedFilterCols,
                queryName,
                effectivePrincipal,
                effectiveSid,
                isAdmin,
                activeConsents,
                sunsetRules,
                tableLookup,
                selectAllColumns: selectAll && tableCols.Count == 0,
                outcome,
                ct).ConfigureAwait(false);
        }

        var fieldCount = Math.Max(1, metadata.ProjectedColumns.Count);
        var joinCost = metadata.JoinCount * _analysisOptions.CostPerJoin;
        var subqueryCost = metadata.MaxSubqueryDepth * _analysisOptions.CostPerSubqueryDepth;
        var fieldCost = fieldCount * _analysisOptions.CostPerRequestedField;
        var unpaginatedPenalty = !metadata.HasExplicitLimit ? _analysisOptions.UnboundedScanPenalty : 0;

        outcome.EstimatedCost = fieldCost + joinCost + subqueryCost + unpaginatedPenalty;
        return outcome;
    }

    private static bool IsBalancedSqlSyntax(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var depth = 0;
        var inSingleQuote = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (ch == '\'')
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (!inSingleQuote)
            {
                if (ch == '(') depth++;
                else if (ch == ')')
                {
                    depth--;
                    if (depth < 0) return false;
                }
            }
        }

        if (inSingleQuote || depth != 0)
        {
            return false;
        }

        if (sql.EndsWith("FROM", StringComparison.OrdinalIgnoreCase) ||
            sql.EndsWith("WHERE", StringComparison.OrdinalIgnoreCase) ||
            sql.EndsWith("JOIN", StringComparison.OrdinalIgnoreCase) ||
            sql.EndsWith("ON", StringComparison.OrdinalIgnoreCase) ||
            sql.EndsWith(",", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string StripSqlAlias(string projected)
    {
        var trimmed = projected.Trim();
        var asIdx = trimmed.LastIndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (asIdx > 0)
        {
            return trimmed[..asIdx].Trim();
        }

        return trimmed;
    }

    private static void AssignColumnReferenceToTable(
        string rawColumnRef,
        IReadOnlyList<TableAccessTarget> referencedTables,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, TableMetadata> tableLookup,
        Dictionary<string, List<string>> targetMap)
    {
        var cleaned = rawColumnRef.Trim().Trim('"', '`', '[', ']');
        var dotIdx = cleaned.IndexOf('.');
        if (dotIdx > 0 && dotIdx < cleaned.Length - 1)
        {
            var prefix = cleaned[..dotIdx].Trim();
            var colName = cleaned[(dotIdx + 1)..].Trim();
            if (aliasToTable.TryGetValue(prefix, out var resolvedTable) &&
                targetMap.TryGetValue(resolvedTable, out var list))
            {
                if (!list.Contains(colName, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(colName);
                }

                return;
            }
        }

        if (referencedTables.Count == 1)
        {
            var onlyKey = !string.IsNullOrWhiteSpace(referencedTables[0].FullName)
                ? referencedTables[0].FullName
                : referencedTables[0].TableName;
            if (!targetMap[onlyKey].Contains(cleaned, StringComparer.OrdinalIgnoreCase))
            {
                targetMap[onlyKey].Add(cleaned);
            }

            return;
        }

        foreach (var target in referencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            if (tableLookup.TryGetValue(key, out var catTable) &&
                catTable.Columns.Any(c => string.Equals(c.ColumnName, cleaned, StringComparison.OrdinalIgnoreCase)))
            {
                if (!targetMap[key].Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                {
                    targetMap[key].Add(cleaned);
                }

                return;
            }
        }

        var firstKey = !string.IsNullOrWhiteSpace(referencedTables[0].FullName)
            ? referencedTables[0].FullName
            : referencedTables[0].TableName;
        if (!targetMap[firstKey].Contains(cleaned, StringComparer.OrdinalIgnoreCase))
        {
            targetMap[firstKey].Add(cleaned);
        }
    }

    private static void ExtractAdditionalSqlFilterColumns(
        string sql,
        IReadOnlyList<TableAccessTarget> referencedTables,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, TableMetadata> tableLookup,
        Dictionary<string, List<string>> filterColumnsByTable)
    {
        foreach (Match match in SqlJoinOnColumnRegex.Matches(sql))
        {
            ExtractColumnTokensFromClause(match.Groups[1].Value, referencedTables, aliasToTable, tableLookup, filterColumnsByTable);
        }

        foreach (Match match in SqlWhereClauseRegex.Matches(sql))
        {
            ExtractColumnTokensFromClause(match.Groups[1].Value, referencedTables, aliasToTable, tableLookup, filterColumnsByTable);
        }
    }

    private static void ExtractColumnTokensFromClause(
        string clause,
        IReadOnlyList<TableAccessTarget> referencedTables,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, TableMetadata> tableLookup,
        Dictionary<string, List<string>> filterColumnsByTable)
    {
        var withoutStrings = Regex.Replace(clause, @"'[^']*'", " ");
        foreach (Match token in IdentifierTokenRegex.Matches(withoutStrings))
        {
            var full = token.Value;
            if (IsSqlKeyword(full))
            {
                continue;
            }

            if (token.Groups[2].Success)
            {
                AssignColumnReferenceToTable(full, referencedTables, aliasToTable, tableLookup, filterColumnsByTable);
            }
            else
            {
                var word = token.Groups[1].Value;
                if (IsSqlKeyword(word))
                {
                    continue;
                }

                foreach (var target in referencedTables)
                {
                    var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
                    if (tableLookup.TryGetValue(key, out var catTable) &&
                        catTable.Columns.Any(c => string.Equals(c.ColumnName, word, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!filterColumnsByTable[key].Contains(word, StringComparer.OrdinalIgnoreCase))
                        {
                            filterColumnsByTable[key].Add(word);
                        }
                    }
                }
            }
        }
    }

    private static HashSet<string> ExtractSqlEqualityFilterColumns(string sql)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var match = SqlWhereClauseRegex.Match(sql);
        if (!match.Success)
        {
            return result;
        }

        var clause = match.Groups[1].Value;
        var eqRegex = new Regex(@"([a-zA-Z_][a-zA-Z0-9_.]*)\s*=\s*('[^']*'|:[a-zA-Z_][a-zA-Z0-9_]*|@[a-zA-Z_][a-zA-Z0-9_]*|[0-9]+)", RegexOptions.IgnoreCase);
        foreach (Match eq in eqRegex.Matches(clause))
        {
            var rawCol = eq.Groups[1].Value;
            var col = ExtractUnqualifiedName(rawCol);
            result.Add(col);
        }

        return result;
    }

    private static bool IsSqlKeyword(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "SELECT" or "FROM" or "WHERE" or "JOIN" or "INNER" or "LEFT" or "RIGHT" or "FULL" or "OUTER" or
            "ON" or "AND" or "OR" or "NOT" or "IN" or "IS" or "NULL" or "LIKE" or "BETWEEN" or "GROUP" or
            "BY" or "ORDER" or "ASC" or "DESC" or "LIMIT" or "OFFSET" or "HAVING" or "AS" or "TRUE" or "FALSE" => true,
            _ => false
        };
    }

    private async Task EvaluateTableAndColumnsAsync(
        string rawDatasetName,
        IReadOnlyList<string> requestedColumns,
        IReadOnlyList<string> filterColumns,
        HashSet<string> providedFilterColumns,
        string queryName,
        ClaimsPrincipal effectivePrincipal,
        Sid effectiveSid,
        bool isAdmin,
        IReadOnlyList<Consent> activeConsents,
        IReadOnlyList<FieldSunsettingRule> sunsetRules,
        Dictionary<string, TableMetadata> tableLookup,
        bool selectAllColumns,
        SingleQueryAnalysisOutcome outcome,
        CancellationToken ct)
    {
        if (!tableLookup.TryGetValue(rawDatasetName, out var table))
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.UnknownTable,
                $"Dataset '{rawDatasetName}' does not exist in the catalog.",
                QueryName: queryName,
                Dataset: rawDatasetName));
            return;
        }

        var canonicalDataset = FormatDatasetId(table.Identifier);
        var accessStatus = CatalogVisibility.ComputeAccessStatus(table.Identifier, activeConsents, pendingRequests: null, isAdmin: isAdmin);
        var hasTableGrant = string.Equals(accessStatus, "GRANTED", StringComparison.OrdinalIgnoreCase);

        var isOwner = false;
        if (_dataOwnershipRepository != null)
        {
            try
            {
                isOwner = await _dataOwnershipRepository.IsAuthorizedApproverForTableAsync(table.Identifier, effectiveSid, ct).ConfigureAwait(false);
            }
            catch
            {
                // Fail-safe
            }
        }

        if (table.Table.Visibility == DatasetVisibility.Hidden && !hasTableGrant && !isOwner && !isAdmin)
        {
            outcome.HiddenMissingDatasetsCount++;
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.UnknownTable,
                "Referenced dataset does not exist or is not visible to the current principal.",
                QueryName: queryName));
            return;
        }

        if (!hasTableGrant && !isOwner && !isAdmin)
        {
            outcome.MissingConsents.Add(new RequestableConsentItem(
                Dataset: canonicalDataset,
                Column: null,
                RequestedLevel: "READ",
                SuggestedPurpose: $"Read access to dataset '{canonicalDataset}' required by query '{queryName}'."));
        }

        var tableAllowConsents = activeConsents
            .Where(c => c.TableIdentifier == table.Identifier && c.Effect == ConsentEffect.Allow)
            .ToList();

        if (!string.IsNullOrWhiteSpace(table.Table.RlsExpression) ||
            tableAllowConsents.Any(c => c.RowFilters is { Count: > 0 } || !string.IsNullOrWhiteSpace(c.RowFilterCondition)))
        {
            outcome.HasRowFilters = true;
        }

        await EvaluateMandatoryVirtualFiltersAsync(
            table,
            canonicalDataset,
            providedFilterColumns,
            effectivePrincipal,
            effectiveSid,
            isAdmin,
            outcome,
            ct).ConfigureAwait(false);

        var columnsToInspect = selectAllColumns
            ? table.Columns.Select(c => c.ColumnName).ToList()
            : requestedColumns.ToList();

        foreach (var colName in columnsToInspect)
        {
            var catalogCol = table.Columns.FirstOrDefault(c =>
                string.Equals(c.ColumnName, colName, StringComparison.OrdinalIgnoreCase));

            if (catalogCol == null)
            {
                outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                    QueryDiagnosticCodes.UnknownColumn,
                    $"Column '{colName}' does not exist on dataset '{canonicalDataset}'.",
                    QueryName: queryName,
                    Dataset: canonicalDataset,
                    Column: colName));
                continue;
            }

            var accessLevel = ResolveEffectiveColumnAccess(
                table,
                catalogCol,
                activeConsents,
                hasTableGrant,
                isOwner,
                isAdmin);

            outcome.OutputColumns.Add(new AnalyzedOutputColumn(
                Dataset: canonicalDataset,
                Column: catalogCol.ColumnName,
                DataType: catalogCol.DataType,
                EffectiveAccessLevel: accessLevel));

            if (accessLevel == ColumnAccessLevel.Mask)
            {
                outcome.MissingConsents.Add(new RequestableConsentItem(
                    Dataset: canonicalDataset,
                    Column: catalogCol.ColumnName,
                    RequestedLevel: "READ_CLEARTEXT",
                    SuggestedPurpose: $"Unmasked cleartext access to column '{canonicalDataset}.{catalogCol.ColumnName}' for query '{queryName}'."));
            }

            CheckDeprecatedField(table.Identifier, canonicalDataset, catalogCol, sunsetRules, outcome.DeprecatedFields);
        }

        foreach (var filterColName in filterColumns.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var catalogCol = table.Columns.FirstOrDefault(c =>
                string.Equals(c.ColumnName, filterColName, StringComparison.OrdinalIgnoreCase));

            if (catalogCol == null)
            {
                if (IsVirtualFilterParameter(table, canonicalDataset, filterColName))
                {
                    continue;
                }

                outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                    QueryDiagnosticCodes.UnknownColumn,
                    $"Filter/join column '{filterColName}' does not exist on dataset '{canonicalDataset}'.",
                    QueryName: queryName,
                    Dataset: canonicalDataset,
                    Column: filterColName));
                continue;
            }

            var filterAccess = ResolveEffectiveColumnAccess(
                table,
                catalogCol,
                activeConsents,
                hasTableGrant,
                isOwner,
                isAdmin);

            if (filterAccess == ColumnAccessLevel.Mask)
            {
                outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                    QueryDiagnosticCodes.MaskedColumnInFilter,
                    $"Column '{canonicalDataset}.{catalogCol.ColumnName}' is masked for the target principal and cannot be used in a WHERE or JOIN predicate without cleartext consent.",
                    QueryName: queryName,
                    Dataset: canonicalDataset,
                    Column: catalogCol.ColumnName));

                outcome.MissingConsents.Add(new RequestableConsentItem(
                    Dataset: canonicalDataset,
                    Column: catalogCol.ColumnName,
                    RequestedLevel: "READ_CLEARTEXT",
                    SuggestedPurpose: $"Cleartext filter/join access on '{canonicalDataset}.{catalogCol.ColumnName}' required by query '{queryName}'."));
            }

            CheckDeprecatedField(table.Identifier, canonicalDataset, catalogCol, sunsetRules, outcome.DeprecatedFields);
        }
    }

    private async Task EvaluateMandatoryVirtualFiltersAsync(
        TableMetadata table,
        string canonicalDataset,
        HashSet<string> providedFilterColumns,
        ClaimsPrincipal effectivePrincipal,
        Sid effectiveSid,
        bool isAdmin,
        SingleQueryAnalysisOutcome outcome,
        CancellationToken ct)
    {
        IReadOnlyList<string>? requiredFilterCols = null;

        if (!_analysisOptions.MandatoryVirtualFilters.TryGetValue(canonicalDataset, out requiredFilterCols) &&
            !_analysisOptions.MandatoryVirtualFilters.TryGetValue($"{table.Identifier.Schema}.{table.Identifier.TableName}", out requiredFilterCols))
        {
            _analysisOptions.MandatoryVirtualFilters.TryGetValue(table.Identifier.TableName, out requiredFilterCols);
        }

        if (requiredFilterCols is { Count: > 0 })
        {
            outcome.HasMandatoryVirtualFilters = true;
            if (!isAdmin && requiredFilterCols.Any(req => !providedFilterColumns.Contains(req)))
            {
                outcome.IsBlockedByMandatoryFilter = true;
            }
        }

        if (_accessProfileRepository != null)
        {
            var tenantId = effectivePrincipal.GetTenantId();
            var profiles = await _accessProfileRepository.GetProfilesForSubjectAsync(tenantId, effectiveSid.Value, ct).ConfigureAwait(false);
            if (profiles.Count > 0)
            {
                outcome.HasMandatoryVirtualFilters = true;
            }
        }
    }

    private bool IsVirtualFilterParameter(TableMetadata table, string canonicalDataset, string filterColName)
    {
        if (_analysisOptions.MandatoryVirtualFilters.TryGetValue(canonicalDataset, out var cols1) &&
            cols1.Contains(filterColName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_analysisOptions.MandatoryVirtualFilters.TryGetValue($"{table.Identifier.Schema}.{table.Identifier.TableName}", out var cols2) &&
            cols2.Contains(filterColName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_analysisOptions.MandatoryVirtualFilters.TryGetValue(table.Identifier.TableName, out var cols3) &&
            cols3.Contains(filterColName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static ColumnAccessLevel ResolveEffectiveColumnAccess(
        TableMetadata table,
        TableColumn column,
        IReadOnlyList<Consent> activeConsents,
        bool hasTableGrant,
        bool isOwner,
        bool isAdmin)
    {
        if (isAdmin || isOwner)
        {
            return ColumnAccessLevel.Clear;
        }

        var tableConsents = activeConsents
            .Where(c => c.TableIdentifier == table.Identifier)
            .ToList();

        var allowConsents = tableConsents
            .Where(c => c.Effect == ConsentEffect.Allow)
            .ToList();

        bool isExplicitlyDenied = tableConsents
            .SelectMany(c => c.ColumnRules)
            .Any(cr => string.Equals(cr.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                       cr.AccessLevel == ColumnAccessLevel.Deny) ||
            (table.ColumnMaskingRules != null &&
             table.ColumnMaskingRules.TryGetValue(column.ColumnName, out var mr) &&
             string.Equals(mr.RuleType, "DENY", StringComparison.OrdinalIgnoreCase));

        if (isExplicitlyDenied)
        {
            return ColumnAccessLevel.Deny;
        }

        bool hasExplicitClear = allowConsents
            .SelectMany(c => c.ColumnRules)
            .Any(cr => string.Equals(cr.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                       cr.AccessLevel == ColumnAccessLevel.Clear);

        bool hasExplicitMask = allowConsents
            .SelectMany(c => c.ColumnRules)
            .Any(cr => string.Equals(cr.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                       cr.AccessLevel == ColumnAccessLevel.Mask);

        bool isSensitiveInCatalog = column.IsSensitive ||
                                    (table.ColumnMaskingRules != null && table.ColumnMaskingRules.ContainsKey(column.ColumnName));

        if ((isSensitiveInCatalog && !hasExplicitClear) || (hasExplicitMask && !hasExplicitClear))
        {
            return ColumnAccessLevel.Mask;
        }

        return hasTableGrant ? ColumnAccessLevel.Clear : ColumnAccessLevel.Deny;
    }

    private static void CheckDeprecatedField(
        TableIdentifier tableId,
        string canonicalDataset,
        TableColumn column,
        IReadOnlyList<FieldSunsettingRule> sunsetRules,
        List<DeprecatedFieldReference> deprecatedFields)
    {
        var dep = Autheris.Application.Mcp.Services.McpDatasetCatalog.ResolveFieldDeprecation(tableId, column, sunsetRules);
        if (dep != null)
        {
            deprecatedFields.Add(new DeprecatedFieldReference(
                Dataset: canonicalDataset,
                Column: column.ColumnName,
                DeprecatedSince: dep.DeprecatedSince,
                SunsetDate: dep.SunsetDate,
                ReplacementField: dep.ReplacementField,
                Reason: dep.Reason));
        }
    }

    private static void MergeOutcome(
        SingleQueryAnalysisOutcome source,
        List<QueryValidationDiagnostic> diagnostics,
        List<AnalyzedOutputColumn> outputColumns,
        HashSet<string> outputColumnKeys,
        List<DeprecatedFieldReference> deprecatedFields,
        HashSet<string> deprecatedKeys,
        List<RequestableConsentItem> missingConsents,
        HashSet<string> missingConsentKeys,
        ref bool hasRowFilters,
        ref bool hasMandatoryVirtualFilters,
        ref bool isBlockedByMandatoryFilter,
        ref int hiddenMissingDatasetsCount,
        ref int totalEstimatedCost)
    {
        diagnostics.AddRange(source.Diagnostics);

        foreach (var col in source.OutputColumns)
        {
            var key = $"{col.Dataset}.{col.Column}";
            if (outputColumnKeys.Add(key))
            {
                outputColumns.Add(col);
            }
        }

        foreach (var dep in source.DeprecatedFields)
        {
            var key = $"{dep.Dataset}.{dep.Column}";
            if (deprecatedKeys.Add(key))
            {
                deprecatedFields.Add(dep);
            }
        }

        foreach (var mc in source.MissingConsents)
        {
            var key = $"{mc.Dataset}:{mc.Column ?? "*"}:{mc.RequestedLevel}";
            if (missingConsentKeys.Add(key))
            {
                missingConsents.Add(mc);
            }
        }

        if (source.HasRowFilters) hasRowFilters = true;
        if (source.HasMandatoryVirtualFilters) hasMandatoryVirtualFilters = true;
        if (source.IsBlockedByMandatoryFilter) isBlockedByMandatoryFilter = true;
        hiddenMissingDatasetsCount += source.HiddenMissingDatasetsCount;
        totalEstimatedCost += source.EstimatedCost;
    }

    private sealed class SingleQueryAnalysisOutcome
    {
        public List<string> ExtractedTables { get; } = new();
        public List<QueryValidationDiagnostic> Diagnostics { get; } = new();
        public List<AnalyzedOutputColumn> OutputColumns { get; } = new();
        public List<DeprecatedFieldReference> DeprecatedFields { get; } = new();
        public List<RequestableConsentItem> MissingConsents { get; } = new();
        public bool HasRowFilters { get; set; }
        public bool HasMandatoryVirtualFilters { get; set; }
        public bool IsBlockedByMandatoryFilter { get; set; }
        public int HiddenMissingDatasetsCount { get; set; }
        public int EstimatedCost { get; set; }
    }
}
