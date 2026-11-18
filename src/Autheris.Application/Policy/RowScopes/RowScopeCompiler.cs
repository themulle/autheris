namespace Autheris.Application.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

/// <summary>
/// Compiles grant row-scope entries, legacy consent row filters, and hard row policies into a single typed AST predicate.
/// </summary>
public sealed partial class RowScopeCompiler
{
    public const string HardPolicyUncoveredCode = "HARD_ROW_POLICY_UNCOVERED";
    public const string HardPolicyRoleRejectedCode = "HARD_ROW_POLICY_ROLE_UNSUPPORTED";
    public const string NoContributingGrantCode = "ROW_SCOPE_NO_CONTRIBUTING_GRANT";

    private readonly IVirtualFilterSnapshotProvider? _snapshots;
    private readonly IVirtualFilterPredicateBuilder _predicates;
    private readonly IVirtualFilterKeySetProvider? _keySetProvider;
    private readonly RowPolicyFactory _rowPolicyFactory;
    private readonly ITableMetadataRepository? _catalog;
    private readonly SqlDataSourceOptions? _dataSources;
    private readonly Autheris.Application.Policy.Classification.ClassificationAccessResolver? _classificationResolver;
    private readonly TimeProvider _timeProvider;

    public RowScopeCompiler(
        IVirtualFilterSnapshotProvider? snapshots = null,
        IVirtualFilterPredicateBuilder? predicates = null,
        IVirtualFilterKeySetProvider? keySetProvider = null,
        RowPolicyFactory? rowPolicyFactory = null,
        ITableMetadataRepository? catalog = null,
        IOptions<GatewayOptions>? options = null,
        Autheris.Application.Policy.Classification.ClassificationAccessResolver? classificationResolver = null,
        TimeProvider? timeProvider = null)
    {
        _snapshots = snapshots;
        _predicates = predicates ?? new StructuredFilterSqlBuilder(options);
        _keySetProvider = keySetProvider;
        _rowPolicyFactory = rowPolicyFactory ?? new RowPolicyFactory(new PolicyPredicateRenderer(), new PolicyExpressionParser(FastSqlEngine.Default));
        _catalog = catalog;
        _dataSources = options?.Value?.DataSources;
        _classificationResolver = classificationResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True when a snapshot provider is configured for hard row policy resolution.</summary>
    public bool CanResolveHardPolicies => _snapshots != null;

    public ValueTask<RowScopeCompileOutcome> CompileAsync(RowScopeCompileRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CompileCoreAsync(request, includeGrants: true, includeHardPolicies: request.IncludeHardPolicies, ct);
    }

    public ValueTask<RowScopeCompileOutcome> CompileGrantsOnlyAsync(RowScopeCompileRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CompileCoreAsync(request, includeGrants: true, includeHardPolicies: false, ct);
    }

    public ValueTask<RowScopeCompileOutcome> CompileHardPoliciesOnlyAsync(
        TenantId tenant,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        TableMetadata metadata,
        FilterObjectKinds objectKind = FilterObjectKinds.Relation,
        IReadOnlySet<Sid>? allUserSids = null,
        VirtualFilterSnapshot? snapshot = null,
        TargetSqlDialect? targetDialect = null,
        CancellationToken ct = default)
    {
        var request = new RowScopeCompileRequest(
            tenant,
            userSid,
            groupSids,
            metadata,
            AllowGrants: [],
            ObjectKind: objectKind,
            AllUserSids: allUserSids,
            Snapshot: snapshot,
            IncludeHardPolicies: true,
            TargetDialect: targetDialect);
        return CompileCoreAsync(request, includeGrants: false, includeHardPolicies: true, ct);
    }

    private async ValueTask<RowScopeCompileOutcome> CompileCoreAsync(
        RowScopeCompileRequest request,
        bool includeGrants,
        bool includeHardPolicies,
        CancellationToken ct)
    {
        VirtualFilterSnapshot? snapshot = request.Snapshot;
        bool needsSnapshot = includeHardPolicies
            || (includeGrants && request.AllowGrants.Any(g => g.RowScope.Count > 0));

        if (snapshot == null && needsSnapshot)
        {
            if (_snapshots == null)
            {
                return RowScopeCompileOutcome.Deny(
                    VirtualFilterErrorCodes.BindingMisconfigured,
                    "Virtual filter snapshot provider is unavailable for row-scope compilation.");
            }

            try
            {
                snapshot = await _snapshots.GetAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return RowScopeCompileOutcome.Deny(
                    VirtualFilterErrorCodes.BindingMisconfigured,
                    $"Virtual filter snapshot could not be loaded ({ex.GetType().Name}).");
            }
        }

        VirtualFilterTenantContext? tenantContext = null;
        if (snapshot != null && _catalog != null)
        {
            try
            {
                tenantContext = await LoadTenantContextAsync(snapshot, request.Tenant, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return RowScopeCompileOutcome.Deny(
                    VirtualFilterErrorCodes.BindingMisconfigured,
                    $"Tenant columns of row-policy source tables could not be resolved ({ex.GetType().Name}).");
            }
        }

        var dialect = request.TargetDialect ?? PolicyParameterNamer.ToTargetDialect(request.Metadata.Dialect);
        var predicatesToAnd = new List<PolicyPredicate>();
        bool isUnfilteredGrantUnion = true;
        int paramSeq = 0;

        if (includeGrants)
        {
            var (grantOutcome, grantPredicate, unfilteredUnion) = await CompileGrantUnionAsync(
                request,
                snapshot,
                tenantContext,
                dialect,
                () => paramSeq++,
                ct).ConfigureAwait(false);

            if (grantOutcome != null)
            {
                return grantOutcome;
            }

            isUnfilteredGrantUnion = unfilteredUnion;
            if (grantPredicate != null)
            {
                predicatesToAnd.Add(grantPredicate);
            }

            if (request.DenyRowFilters is { Count: > 0 })
            {
                var denyPred = BuildDenyRowFiltersPredicate(request.DenyRowFilters, request.Metadata, dialect);
                if (denyPred != null)
                {
                    predicatesToAnd.Add(denyPred);
                }
            }
        }

        IReadOnlyList<string> appliedHardPolicies = Array.Empty<string>();
        IReadOnlyList<AppliedVirtualFilterBinding> appliedHardBindings = Array.Empty<AppliedVirtualFilterBinding>();

        if (includeHardPolicies && snapshot != null && snapshot.Profiles.Count > 0)
        {
            var (hardOutcome, hardPredicate, hardNames, hardBindings) = await CompileHardPoliciesInternalAsync(
                request,
                snapshot,
                tenantContext,
                dialect,
                () => paramSeq++,
                ct).ConfigureAwait(false);

            if (hardOutcome != null)
            {
                return hardOutcome;
            }

            appliedHardPolicies = hardNames;
            appliedHardBindings = hardBindings;
            if (hardPredicate != null)
            {
                predicatesToAnd.Add(hardPredicate);
            }
        }

        if (request.AdditionalNarrowingPredicates is { Count: > 0 })
        {
            predicatesToAnd.AddRange(request.AdditionalNarrowingPredicates);
        }

        if (predicatesToAnd.Count == 0)
        {
            return new RowScopeCompileOutcome(
                IsDenied: false,
                DenyReason: null,
                DenyCode: null,
                DenyStep: null,
                Predicate: null,
                RowPolicy: null,
                AppliedHardPolicies: appliedHardPolicies,
                AppliedHardBindings: appliedHardBindings,
                CallerAttributes: Array.Empty<string>(),
                IsUnfilteredGrantUnion: isUnfilteredGrantUnion);
        }

        var combined = predicatesToAnd.Aggregate(static (l, r) => l.And(r));
        var callerAttrs = CallerAttributeBinder.SlotAttributes(combined)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var rowPolicy = _rowPolicyFactory.FromPredicate(combined, dialect);
        return new RowScopeCompileOutcome(
            IsDenied: false,
            DenyReason: null,
            DenyCode: null,
            DenyStep: null,
            Predicate: combined,
            RowPolicy: rowPolicy,
            AppliedHardPolicies: appliedHardPolicies,
            AppliedHardBindings: appliedHardBindings,
            CallerAttributes: callerAttrs,
            IsUnfilteredGrantUnion: isUnfilteredGrantUnion);
    }

    private async ValueTask<(RowScopeCompileOutcome? Denial, PolicyPredicate? Predicate, bool UnfilteredUnion)> CompileGrantUnionAsync(
        RowScopeCompileRequest request,
        VirtualFilterSnapshot? snapshot,
        VirtualFilterTenantContext? tenantContext,
        TargetSqlDialect dialect,
        Func<int> nextSeq,
        CancellationToken ct)
    {
        if (request.AllowGrants.Count == 0)
        {
            return (RowScopeCompileOutcome.Deny(NoContributingGrantCode, "No allow grants supplied."), null, false);
        }

        var filtersByName = snapshot?.Filters
            .Where(f => f.TenantId == request.Tenant)
            .ToDictionary(f => f.Name, StringComparer.Ordinal)
            ?? new Dictionary<string, VirtualFilter>(StringComparer.Ordinal);

        var evaluatedGrants = new List<EvaluatedGrant>();
        foreach (var grant in request.AllowGrants)
        {
            var (denial, resolvedEntries) = ResolveGrantEntries(grant, filtersByName, request);
            if (denial != null)
            {
                return (denial, null, false);
            }

            if (resolvedEntries == null)
            {
                // At least one entry was uncovered with UncoveredPolicy.Deny -> grant does not contribute.
                continue;
            }

            evaluatedGrants.Add(new EvaluatedGrant(grant, resolvedEntries));
        }

        if (evaluatedGrants.Count == 0)
        {
            return (
                RowScopeCompileOutcome.Deny(
                    NoContributingGrantCode,
                    $"No contributing grant covers target table '{request.Metadata.Identifier}'."),
                null,
                false);
        }

        if (evaluatedGrants.Any(g => g.Entries.Count == 0 && (g.Grant.LegacyRowFilters is null or { Count: 0 })))
        {
            // An unfiltered grant collapses the union across grants to unrestricted (TRUE).
            return (null, null, true);
        }

        var (singleEntriesToCompile, passthroughGrants) = MergeSingleEntryGrants(evaluatedGrants);
        var grantBranches = new List<PolicyPredicate>();

        foreach (var entry in singleEntriesToCompile)
        {
            try
            {
                var pred = await CompileSinglePolicyEntryAsync(
                    entry.Policy,
                    entry.Entry.ColumnMap,
                    entry.Entry.Parameters,
                    request.Metadata,
                    request.Tenant,
                    dialect,
                    tenantContext,
                    $"__gql_rs_{nextSeq().ToString(CultureInfo.InvariantCulture)}_",
                    ct).ConfigureAwait(false);
                grantBranches.Add(pred);
            }
            catch (VirtualFilterBindingException ex)
            {
                return (RowScopeCompileOutcome.Deny(ex.Code, ex.Message), null, false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or GatewaySecurityException)
            {
                var code = ex is GatewaySecurityException gse ? gse.ErrorCode : VirtualFilterErrorCodes.BindingMisconfigured;
                return (RowScopeCompileOutcome.Deny(code, ex.Message), null, false);
            }
        }

        foreach (var grant in passthroughGrants)
        {
            var parts = new List<PolicyPredicate>();
            foreach (var entry in grant.Entries)
            {
                try
                {
                    var pred = await CompileSinglePolicyEntryAsync(
                        entry.Policy,
                        entry.Entry.ColumnMap,
                        entry.Entry.Parameters,
                        request.Metadata,
                        request.Tenant,
                        dialect,
                        tenantContext,
                        $"__gql_rs_{nextSeq().ToString(CultureInfo.InvariantCulture)}_",
                        ct).ConfigureAwait(false);
                    parts.Add(pred);
                }
                catch (VirtualFilterBindingException ex)
                {
                    return (RowScopeCompileOutcome.Deny(ex.Code, ex.Message), null, false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or GatewaySecurityException)
                {
                    var code = ex is GatewaySecurityException gse ? gse.ErrorCode : VirtualFilterErrorCodes.BindingMisconfigured;
                    return (RowScopeCompileOutcome.Deny(code, ex.Message), null, false);
                }
            }

            if (grant.Grant.LegacyRowFilters is { Count: > 0 } legacy)
            {
                var legacyPred = BuildLegacyFiltersPredicate(legacy, request.Metadata, dialect, nextSeq());
                parts.Add(legacyPred);
            }

            if (parts.Count > 0)
            {
                grantBranches.Add(parts.Aggregate(static (l, r) => l.And(r)));
            }
        }

        if (grantBranches.Count == 0)
        {
            return (null, null, true);
        }

        var union = grantBranches.Aggregate(static (l, r) => l.Or(r));
        return (null, union, false);
    }

    private static (RowScopeCompileOutcome? Denial, List<ResolvedEntry>? Entries) ResolveGrantEntries(
        RowScopeGrant grant,
        Dictionary<string, VirtualFilter> filtersByName,
        RowScopeCompileRequest request)
    {
        var resolved = new List<ResolvedEntry>(grant.RowScope.Count);
        foreach (var entry in grant.RowScope)
        {
            if (!filtersByName.TryGetValue(entry.PolicyName, out var filter))
            {
                return (RowScopeCompileOutcome.Deny(GrantRowScopeValidator.PolicyNotFoundCode, $"Row policy '{entry.PolicyName}' does not exist in tenant."), null);
            }

            if (filter.Status != FilterApprovalStatus.Active)
            {
                return (RowScopeCompileOutcome.Deny(GrantRowScopeValidator.PolicyNotActiveCode, $"Row policy '{entry.PolicyName}' is not active (status: {filter.Status})."), null);
            }

            IReadOnlyDictionary<string, IReadOnlyList<PolicyValue>> boundParams;
            try
            {
                RowPolicyParameterBinder.EnsureCrossSourceAllowed(filter, request.Metadata.Identifier.Domain);
                RowPolicyParameterBinder.ValidateFilterDeclarations(filter);
                boundParams = RowPolicyParameterBinder.BindValues(filter, entry.Parameters);
            }
            catch (VirtualFilterBindingException ex)
            {
                return (RowScopeCompileOutcome.Deny(ex.Code, ex.Message), null);
            }

            IReadOnlyList<string> requiredCols;
            try
            {
                var binding = new FilterBinding
                {
                    FilterName = filter.Name,
                    ObjectKinds = request.ObjectKind,
                    ColumnMap = entry.ColumnMap
                };
                requiredCols = VirtualFilterColumns.RequiredTargetColumns(filter, binding);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                return (RowScopeCompileOutcome.Deny(VirtualFilterErrorCodes.BindingMisconfigured, ex.Message), null);
            }

            bool allPresent = requiredCols.All(c => request.Metadata.HasColumn(c));
            if (!allPresent)
            {
                if (entry.Uncovered == UncoveredPolicy.Deny)
                {
                    // Uncovered Deny: grant does not contribute for this object.
                    return (null, null);
                }

                // Uncovered Skip: drop only this entry.
                continue;
            }

            resolved.Add(new ResolvedEntry(filter, entry, boundParams));
        }

        return (null, resolved);
    }

    private static (List<ResolvedEntry> Merged, List<EvaluatedGrant> Remaining) MergeSingleEntryGrants(
        List<EvaluatedGrant> evaluatedGrants)
    {
        var remaining = new List<EvaluatedGrant>();
        var grouped = new Dictionary<MergeGroupKey, List<ResolvedEntry>>();

        foreach (var g in evaluatedGrants)
        {
            if (g.Entries.Count == 1
                && (g.Grant.LegacyRowFilters is null or { Count: 0 })
                && TryGetMergeableParameter(g.Entries[0], out var paramName, out var listParam))
            {
                var key = new MergeGroupKey(
                    g.Entries[0].Policy.Name,
                    NormalizeColumnMapKey(g.Entries[0].Entry.ColumnMap),
                    paramName!,
                    BuildFixedParamsSignature(g.Entries[0].BoundParameters, paramName!));
                if (!grouped.TryGetValue(key, out var list))
                {
                    list = [];
                    grouped[key] = list;
                }

                list.Add(g.Entries[0]);
            }
            else
            {
                remaining.Add(g);
            }
        }

        var merged = new List<ResolvedEntry>();
        foreach (var (key, items) in grouped)
        {
            if (items.Count == 1)
            {
                merged.Add(items[0]);
                continue;
            }

            var template = items[0];
            var listParam = template.Policy.Parameters.First(p => string.Equals(p.Name, key.MergeParamName, StringComparison.OrdinalIgnoreCase));
            int maxValues = listParam.MaxValues;
            var currentValues = new List<PolicyValue>();
            var currentSeen = new HashSet<PolicyValue>();

            foreach (var item in items)
            {
                var incoming = item.BoundParameters[key.MergeParamName];
                var candidateNew = incoming.Where(v => !currentSeen.Contains(v)).ToList();
                if (currentValues.Count > 0 && currentValues.Count + candidateNew.Count > maxValues)
                {
                    merged.Add(WithMergedListParameter(template, listParam, currentValues));
                    currentValues.Clear();
                    currentSeen.Clear();
                }

                foreach (var val in incoming)
                {
                    if (currentSeen.Add(val))
                    {
                        currentValues.Add(val);
                    }
                }
            }

            if (currentValues.Count > 0)
            {
                merged.Add(WithMergedListParameter(template, listParam, currentValues));
            }
        }

        return (merged, remaining);
    }

    private static bool TryGetMergeableParameter(
        ResolvedEntry resolved,
        out string? mergeParamName,
        out RowPolicyParameter? listParam)
    {
        mergeParamName = null;
        listParam = null;

        var policy = resolved.Policy;
        if (policy.EvaluationMode != RowPolicyEvaluationMode.Live || policy.Structured == null || policy.Parameters.Count == 0)
        {
            return false;
        }

        var listParams = policy.Parameters.Where(p => p.IsList).ToList();
        if (listParams.Count != 1)
        {
            return false;
        }

        var candidate = listParams[0];
        // Merging across OR grants is only semantically equivalent when used solely with positive equality (Eq -> IN).
        bool usedOutsideEq = policy.Structured.Where.Any(c =>
            RowPolicyParameterBinder.TryExtractStructuredPlaceholder(c.Value, out var name)
            && string.Equals(name, candidate.Name, StringComparison.OrdinalIgnoreCase)
            && c.Operator != FilterConditionOperator.Eq);
        if (usedOutsideEq)
        {
            return false;
        }

        mergeParamName = candidate.Name;
        listParam = candidate;
        return true;
    }

    private static ResolvedEntry WithMergedListParameter(
        ResolvedEntry template,
        RowPolicyParameter listParam,
        List<PolicyValue> values)
    {
        var updatedJson = new Dictionary<string, JsonElement>(template.Entry.Parameters, StringComparer.OrdinalIgnoreCase);
        var jsonArray = SerializePolicyValues(listParam.Type, values);
        // Preserve existing key casing if present.
        var existingKey = updatedJson.Keys.FirstOrDefault(k => string.Equals(k, listParam.Name, StringComparison.OrdinalIgnoreCase))
            ?? listParam.Name;
        updatedJson[existingKey] = jsonArray;

        var updatedBound = new Dictionary<string, IReadOnlyList<PolicyValue>>(template.BoundParameters, StringComparer.OrdinalIgnoreCase)
        {
            [listParam.Name] = values.ToList()
        };

        return template with
        {
            Entry = template.Entry with { Parameters = updatedJson },
            BoundParameters = updatedBound
        };
    }

    private static JsonElement SerializePolicyValues(RowPolicyParameterType type, IReadOnlyList<PolicyValue> values)
    {
        object?[] raw = new object?[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            raw[i] = values[i].Value is DateOnly d
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : values[i].Value;
        }

        _ = type;
        return JsonSerializer.SerializeToElement(raw);
    }

    private static string NormalizeColumnMapKey(IReadOnlyDictionary<string, string>? columnMap)
    {
        if (columnMap is null or { Count: 0 })
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            columnMap
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key.ToLowerInvariant()}={kv.Value.ToLowerInvariant()}"));
    }

    private static string BuildFixedParamsSignature(
        IReadOnlyDictionary<string, IReadOnlyList<PolicyValue>> bound,
        string excludedParam)
    {
        var parts = new List<string>();
        foreach (var kv in bound.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(kv.Key, excludedParam, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var vals = string.Join(",", kv.Value.Select(v => $"{(int)v.Type}:{Convert.ToString(v.Value, CultureInfo.InvariantCulture)}"));
            parts.Add($"{kv.Key.ToLowerInvariant()}=[{vals}]");
        }

        return string.Join(";", parts);
    }

    private async ValueTask<PolicyPredicate> CompileSinglePolicyEntryAsync(
        VirtualFilter filter,
        IReadOnlyDictionary<string, string>? columnMap,
        IReadOnlyDictionary<string, JsonElement>? parameterValues,
        TableMetadata target,
        TenantId tenant,
        TargetSqlDialect dialect,
        VirtualFilterTenantContext? tenantContext,
        string parameterPrefix,
        CancellationToken ct)
    {
        if (filter.EvaluationMode == RowPolicyEvaluationMode.KeySet)
        {
            return await CompileKeySetFilterAsync(filter, columnMap, target, tenant, dialect, parameterPrefix, ct).ConfigureAwait(false);
        }

        var binding = new FilterBinding
        {
            FilterName = filter.Name,
            ColumnMap = columnMap
        };
        return BuildLiveTypedPredicate(filter, binding, target, tenantContext, parameterPrefix, parameterValues);
    }

    private PolicyPredicate BuildLiveTypedPredicate(
        VirtualFilter filter,
        FilterBinding binding,
        TableMetadata target,
        VirtualFilterTenantContext? tenantContext,
        string parameterPrefix,
        IReadOnlyDictionary<string, JsonElement>? parameterValues)
    {
        if (_predicates is StructuredFilterSqlBuilder concrete)
        {
            return concrete.BuildTyped(
                    filter,
                    binding,
                    target,
                    target.Dialect,
                    parameterPrefix,
                    tenantContext,
                    parameterValues)
                ?? throw new InvalidOperationException($"Policy '{filter.Name}' produced no typed predicate.");
        }

        if (parameterValues is { Count: > 0 })
        {
            throw new InvalidOperationException($"Predicate builder '{_predicates.GetType().Name}' does not support parameterized policies.");
        }

        return (tenantContext == null
            ? _predicates.BuildTyped(filter, binding, target, target.Dialect, parameterPrefix)
            : _predicates.BuildTyped(filter, binding, target, target.Dialect, parameterPrefix, tenantContext))
            ?? throw new InvalidOperationException($"Policy '{filter.Name}' produced no typed predicate.");
    }

    internal IRowPolicy BuildRowPolicy(PolicyPredicate predicate, DatabaseDialect dialect) =>
        _rowPolicyFactory.FromPredicate(predicate, dialect);

    private static PolicyPredicate BuildLegacyFiltersPredicate(
        IReadOnlyList<ConsentRowFilter> filters,
        TableMetadata metadata,
        TargetSqlDialect dialect,
        int seq)
    {
        var namer = new PolicyParameterNamer($"__gql_rf_{seq.ToString(CultureInfo.InvariantCulture)}_");
        var tableIdentity = new TableIdentity(metadata.Identifier.Schema, metadata.Identifier.TableName);
        return new TypedRowFilterBuilder().Build(filters, tableIdentity, dialect, namer);
    }

    private static PolicyPredicate? BuildDenyRowFiltersPredicate(
        IReadOnlyList<IReadOnlyList<ConsentRowFilter>> denyFilters,
        TableMetadata metadata,
        TargetSqlDialect dialect)
    {
        if (denyFilters.Count == 0)
        {
            return null;
        }

        var tableIdentity = new TableIdentity(metadata.Identifier.Schema, metadata.Identifier.TableName);
        return new TypedRowFilterBuilder().BuildCombined([], denyFilters, tableIdentity, dialect);
    }

    private async ValueTask<VirtualFilterTenantContext?> LoadTenantContextAsync(
        VirtualFilterSnapshot snapshot,
        TenantId tenant,
        CancellationToken ct)
    {
        if (_catalog == null)
        {
            return null;
        }

        var tables = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in snapshot.Filters.Where(f => f.TenantId == tenant))
        {
            IEnumerable<TableIdentifier> sources;
            if (filter.Structured != null)
            {
                sources = filter.Structured.Joins.Select(j => j.Table).Prepend(filter.Structured.From);
            }
            else if (filter.Sql != null)
            {
                try
                {
                    sources = SqlFilterCompiler.ReferencedTables(filter);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            foreach (var source in sources)
            {
                tables[source.ToString()] = source;
            }
        }

        var columns = new Dictionary<string, (string? Column, string? Error)>(StringComparer.OrdinalIgnoreCase);
        var requested = tables.Values.ToList();
        var resolved = await _catalog.GetTableMetadataBatchAsync(requested, ct).ConfigureAwait(false);
        var tableIndex = 0;
        foreach (var (name, identifier) in tables)
        {
            var meta = resolved[tableIndex++];
            if (meta == null)
            {
                columns[name] = (null, $"the source table '{name}' is unknown in the catalog");
                continue;
            }

            try
            {
                columns[name] = (VirtualFilterTenantColumns.Resolve(meta, _dataSources?.RequireTenantColumn == true, _dataSources?.TenantColumnExemptTables), null);
            }
            catch (InvalidOperationException ex)
            {
                columns[name] = (null, ex.Message);
            }
        }

        return new VirtualFilterTenantContext(tenant.Value, identifier =>
        {
            if (!columns.TryGetValue(identifier.ToString(), out var entry))
            {
                throw new InvalidOperationException($"The source table '{identifier}' is unknown in the catalog.");
            }

            return entry.Error != null ? throw new InvalidOperationException(entry.Error) : entry.Column;
        });
    }

    private sealed record ResolvedEntry(
        VirtualFilter Policy,
        GrantRowScopeEntry Entry,
        IReadOnlyDictionary<string, IReadOnlyList<PolicyValue>> BoundParameters);

    private sealed record EvaluatedGrant(
        RowScopeGrant Grant,
        List<ResolvedEntry> Entries);

    private sealed record MergeGroupKey(
        string PolicyName,
        string NormalizedColumnMap,
        string MergeParamName,
        string FixedParamsSignature);
}
