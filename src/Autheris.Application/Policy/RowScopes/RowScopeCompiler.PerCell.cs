namespace Autheris.Application.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

public sealed partial class RowScopeCompiler
{
    /// <summary>
    /// Groups contributing allow consents by effective column signature, compiles each signature's unioned row scope,
    /// and projects either a flat single-signature decision or a per-cell column policy.
    /// </summary>
    internal async ValueTask<PerCellCompileResult> CompilePerCellConsentsAsync(
        TableAccessDecision baseDecision,
        TableAccessQuery query,
        IReadOnlyList<Consent> activeConsents,
        IReadOnlyList<Consent> contributingAllows,
        ICallerAttributeResolver? callerAttributes,
        CancellationToken ct)
    {
        var table = query.Metadata.Identifier;
        var metadata = query.Metadata;
        var dialect = PolicyParameterNamer.ToTargetDialect(metadata.Dialect);
        var now = _timeProvider.GetUtcNow();

        var callerScopes = ResolveCallerScopes(query);
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, Autheris.Application.Policy.Classification.ColumnClassificationGrantOutcome>>? perGrantOutcomes = null;
        IReadOnlyDictionary<string, Autheris.Application.Policy.Classification.ColumnClassificationGrantOutcome>? combinedClassWinners = null;
        if (_classificationResolver is not null &&
            (metadata.ColumnClassifications.Count > 0 ||
             _classificationResolver.HasRepository))
        {
            (perGrantOutcomes, combinedClassWinners) = await _classificationResolver.ResolveForTableAsync(
                metadata, contributingAllows, query.Tenant, callerScopes, columnRowPrefetch: query.ColumnRowPrefetch, unmask: query.Unmask, ct: ct).ConfigureAwait(false);
        }

        var (hardDenied, hardMasked) = CollectFilterlessDenyColumnCaps(
            activeConsents, table, now, query.UserSid, query.GroupSids, query.Roles, query.AllUserSids, callerScopes);

        var denyRowFilters = activeConsents
            .Where(c => c.Effect == ConsentEffect.Deny && c.RowFilters.Count > 0 &&
                        ConsentResolutionService.IsApplicable(c, table, now, query.UserSid, query.GroupSids, query.Roles, query.AllUserSids))
            .Select(static c => c.RowFilters)
            .ToList();

        var columnUniverse = CollectColumnUniverse(metadata, contributingAllows);
        var groups = new Dictionary<string, SignatureGroup>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();

        foreach (var allow in contributingAllows)
        {
            IReadOnlyDictionary<string, Autheris.Application.Policy.Classification.ColumnClassificationGrantOutcome>? grantClassOutcomes = null;
            perGrantOutcomes?.TryGetValue(allow.Id, out grantClassOutcomes);
            var (colMap, implicitMasked) = ComputeGrantEffectiveColumns(
                allow, metadata, columnUniverse, callerScopes, hardDenied, hardMasked, baseDecision.ScopeClearance, grantClassOutcomes);

            string sigKey = PerCellColumnPolicy.BuildSignatureKey(colMap);
            if (!groups.TryGetValue(sigKey, out var group))
            {
                group = new SignatureGroup(sigKey, colMap, new HashSet<string>(implicitMasked, StringComparer.OrdinalIgnoreCase), []);
                groups[sigKey] = group;
                orderedKeys.Add(sigKey);
            }
            else
            {
                group.ImplicitMasked.IntersectWith(implicitMasked);
            }

            group.Grants.Add(RowScopeCompileRequest.FromConsent(allow));
        }

        // Single signature: compile flat union without CASE WHEN or RowDependent state.
        if (orderedKeys.Count <= 1)
        {
            var singleOutcome = await CompileGrantsOnlyAsync(new RowScopeCompileRequest(
                query.Tenant,
                query.UserSid,
                query.GroupSids,
                metadata,
                contributingAllows.Select(RowScopeCompileRequest.FromConsent).ToList(),
                DenyRowFilters: denyRowFilters,
                ObjectKind: query.ObjectKind,
                AllUserSids: query.AllUserSids,
                IncludeHardPolicies: false), ct).ConfigureAwait(false);

            if (singleOutcome.IsDenied)
            {
                return DeniedPerCell(table, singleOutcome);
            }

            var cleared = baseDecision with
            {
                CombinedRowFilterSql = null,
                RowFilterParameters = null,
                ConsentFilterRecipe = null,
                ColumnAccess = groups[orderedKeys[0]].ColumnAccess,
                ColumnMasks = BuildClassificationMaskPolicy(combinedClassWinners) ?? baseDecision.ColumnMasks
            };
            if (singleOutcome.Predicate == null)
            {
                return new PerCellCompileResult(cleared, []);
            }

            var (boundPred, singleAudit) = await ResolveAttributesAsync(singleOutcome.Predicate, singleOutcome.CallerAttributes, query, callerAttributes, ct).ConfigureAwait(false);
            var singlePolicy = _rowPolicyFactory.FromPredicate(boundPred, dialect);
            return new PerCellCompileResult(cleared.WithAdditionalRowPolicy(singlePolicy), singleAudit);
        }

        VirtualFilterSnapshot? snapshot = null;
        if (_snapshots is not null)
        {
            snapshot = await _snapshots.GetAsync(ct).ConfigureAwait(false);
        }
        var tenantCtx = snapshot is not null ? await LoadTenantContextAsync(snapshot, query.Tenant, ct).ConfigureAwait(false) : null;
        var renderer = new PolicyPredicateRenderer();
        int paramSeq = 0;
        Func<int> nextSeq = () => paramSeq++;

        var sigSpecs = new List<PerCellSignatureSpec>(orderedKeys.Count);
        var sigScopes = new List<GrantSignatureScope>(orderedKeys.Count);
        var implicitPerSig = new List<IReadOnlySet<string>>(orderedKeys.Count);
        var allAudits = new List<CallerAttributeAuditEntry>();
        var auditKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string key in orderedKeys)
        {
            var group = groups[key];
            var groupReq = new RowScopeCompileRequest(
                query.Tenant,
                query.UserSid,
                query.GroupSids,
                metadata,
                group.Grants,
                ObjectKind: query.ObjectKind,
                AllUserSids: query.AllUserSids,
                IncludeHardPolicies: false);

            var (denial, groupPred, unfilteredUnion) = await CompileGrantUnionAsync(
                groupReq, snapshot, tenantCtx, dialect, nextSeq, ct).ConfigureAwait(false);
            if (denial is not null)
            {
                return DeniedPerCell(table, denial);
            }

            bool isUnfiltered = unfilteredUnion || groupPred is null;
            PolicyPredicate sigPred;
            if (isUnfiltered)
            {
                sigPred = PerCellColumnPolicy.TautologyPredicate();
            }
            else
            {
                var callerAttrs = CallerAttributeBinder.SlotAttributes(groupPred!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                var (bound, audits) = await ResolveAttributesAsync(groupPred!, callerAttrs, query, callerAttributes, ct).ConfigureAwait(false);
                sigPred = bound;
                foreach (var a in audits)
                {
                    if (auditKeys.Add($"{a.Id}:{a.Outcome}"))
                    {
                        allAudits.Add(a);
                    }
                }
            }

            sigSpecs.Add(new PerCellSignatureSpec(sigPred, isUnfiltered));
            var scopePolicy = new PolicyPredicateRowPolicy(sigPred, dialect, renderer);
            sigScopes.Add(new GrantSignatureScope(key, group.ColumnAccess, scopePolicy));
            implicitPerSig.Add(group.ImplicitMasked);
        }

        PolicyPredicate? denyPredicate = denyRowFilters.Count > 0
            ? BuildDenyRowFiltersPredicate(denyRowFilters, metadata, dialect)
            : null;

        var perCellPolicy = PerCellColumnPolicy.RebuildCore(
            sigSpecs,
            sigScopes,
            implicitPerSig,
            denyPredicate,
            dialect,
            out var combinedColumnAccess,
            out var rowDependentColumns);

        var clearedBase = baseDecision with
        {
            CombinedRowFilterSql = null,
            RowFilterParameters = null,
            ConsentFilterRecipe = null,
            ColumnAccess = combinedColumnAccess,
            RowDependentColumns = rowDependentColumns.Count > 0 ? rowDependentColumns : null,
            ColumnSignatures = perCellPolicy is not null ? sigScopes : null,
            PerCellColumns = perCellPolicy,
            ColumnMasks = BuildClassificationMaskPolicy(combinedClassWinners) ?? baseDecision.ColumnMasks
        };

        PolicyPredicate? combinedRowPred = perCellPolicy?.AnySignatureUnfiltered == true
            ? denyPredicate
            : CombineOrAndNarrow(sigSpecs.Select(static s => s.Scope), denyPredicate);

        if (combinedRowPred is not null)
        {
            var overallRowPolicy = _rowPolicyFactory.FromPredicate(combinedRowPred, dialect);
            // Set RowPolicy directly via WithAdditionalRowPolicy without re-wrapping PerCellColumns' narrowing.
            var text = overallRowPolicy.Project();
            clearedBase = clearedBase with
            {
                RowPolicy = overallRowPolicy,
                CombinedRowFilterSql = text.Sql,
                RowFilterParameters = text.Parameters
            };
        }

        return new PerCellCompileResult(clearedBase, allAudits);
    }

    private static PerCellCompileResult DeniedPerCell(TableIdentifier table, RowScopeCompileOutcome outcome) =>
        new(TableAccessDecision.Denied(table, outcome.DenyReason ?? "Denied by row scope.") with
        {
            DenialKind = TableAccessDenialKind.Policy,
            Reasons = [new DecisionReason(outcome.DenyStep ?? DecisionStep.DefaultDeny, outcome.DenyCode ?? "ROW_SCOPE_DENIED", "RowScope", null)]
        }, []);

    private static async Task<(PolicyPredicate Predicate, IReadOnlyList<CallerAttributeAuditEntry> Audit)> ResolveAttributesAsync(
        PolicyPredicate predicate,
        IReadOnlyList<string> requiredAttributes,
        TableAccessQuery query,
        ICallerAttributeResolver? callerAttributes,
        CancellationToken ct)
    {
        if (requiredAttributes.Count == 0)
        {
            return (predicate, []);
        }

        var (bound, audit) = await TableAccessPolicy.BindVirtualFilterAttributesCoreAsync(predicate, requiredAttributes, query, callerAttributes, ct).ConfigureAwait(false);
        return (bound, audit ?? []);
    }

    private static PolicyPredicate? CombineOrAndNarrow(IEnumerable<PolicyPredicate> branches, PolicyPredicate? narrowing)
    {
        PolicyPredicate? union = null;
        foreach (var branch in branches)
        {
            union = union is null ? branch : union.Or(branch);
        }

        if (union is null)
        {
            return narrowing;
        }

        return narrowing is null ? union : union.And(narrowing);
    }

    private static HashSet<string> ResolveCallerScopes(TableAccessQuery query)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claims = query.Claims ?? query.Principal?.Claims;
        if (claims is null)
        {
            return scopes;
        }

        foreach (var claim in claims)
        {
            if (string.Equals(claim.Type, "scp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, "scope", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var token in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    scopes.Add(token);
                }
            }
        }

        return scopes;
    }

    private static (HashSet<string> HardDenied, HashSet<string> HardMasked) CollectFilterlessDenyColumnCaps(
        IReadOnlyList<Consent> activeConsents,
        TableIdentifier table,
        DateTimeOffset now,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        IReadOnlySet<string> roles,
        IReadOnlySet<Sid>? allUserSids,
        IReadOnlySet<string> callerScopes)
    {
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var consent in activeConsents)
        {
            if (consent.Effect != ConsentEffect.Deny ||
                consent.RowFilters.Count > 0 ||
                consent.RowScope.Count > 0 ||
                !ConsentResolutionService.IsApplicable(consent, table, now, userSid, groupSids, roles, allUserSids))
            {
                continue;
            }

            foreach (var rule in consent.ColumnRules)
            {
                if (!ConsentResolutionService.RuleAppliesToScopes(rule, callerScopes))
                {
                    continue;
                }

                if (rule.AccessLevel == ColumnAccessLevel.Deny)
                {
                    denied.Add(rule.ColumnName);
                }
                else if (rule.AccessLevel == ColumnAccessLevel.Mask)
                {
                    masked.Add(rule.ColumnName);
                }
            }
        }

        return (denied, masked);
    }

    private static List<string> CollectColumnUniverse(TableMetadata metadata, IReadOnlyList<Consent> allows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var col in metadata.Columns)
        {
            if (seen.Add(col.ColumnName))
            {
                result.Add(col.ColumnName);
            }
        }

        foreach (var allow in allows)
        {
            foreach (var rule in allow.ColumnRules)
            {
                if (seen.Add(rule.ColumnName))
                {
                    result.Add(rule.ColumnName);
                }
            }
        }

        return result;
    }

    private static (Dictionary<string, ColumnAccessLevel> Map, HashSet<string> ImplicitMasked) ComputeGrantEffectiveColumns(
        Consent allow,
        TableMetadata metadata,
        IReadOnlyList<string> columnUniverse,
        IReadOnlySet<string> callerScopes,
        IReadOnlySet<string> hardDenied,
        IReadOnlySet<string> hardMasked,
        RestrictedScopeClearance? clearance,
        IReadOnlyDictionary<string, Autheris.Application.Policy.Classification.ColumnClassificationGrantOutcome>? classOutcomes = null)
    {
        var map = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        var implicitMasked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string colName in columnUniverse)
        {
            if (hardDenied.Contains(colName))
            {
                map[colName] = ColumnAccessLevel.Deny;
                continue;
            }

            var metaCol = metadata.GetColumn(colName);
            if (allow.IsPatternGrant && metaCol?.DiscoveryHidden == true)
            {
                map[colName] = ColumnAccessLevel.Deny;
                continue;
            }

            var raw = ConsentResolutionService.ResolveConsentColumnLevel(allow, colName, callerScopes);
            if (hardMasked.Contains(colName) && raw == ColumnAccessLevel.Clear)
            {
                raw = ColumnAccessLevel.Mask;
            }

            if (raw == ColumnAccessLevel.Deny)
            {
                map[colName] = ColumnAccessLevel.Deny;
                continue;
            }

            Autheris.Application.Policy.Classification.ColumnClassificationGrantOutcome? classOutcome = null;
            if (classOutcomes is not null && classOutcomes.TryGetValue(colName, out classOutcome))
            {
                if (classOutcome.AccessLevel == ColumnAccessLevel.Deny)
                {
                    map[colName] = ColumnAccessLevel.Deny;
                    continue;
                }

                if (classOutcome.AccessLevel == ColumnAccessLevel.Mask && raw == ColumnAccessLevel.Clear)
                {
                    raw = ColumnAccessLevel.Mask;
                }
            }

            bool classified = ClassificationMetadata.IsColumnSensitive(metadata, metaCol, colName);
            bool hasExplicitClear = raw == ColumnAccessLevel.Clear &&
                (classOutcome?.AccessLevel == ColumnAccessLevel.Clear || allow.ColumnRules.Any(r =>
                    string.Equals(r.ColumnName, colName, StringComparison.OrdinalIgnoreCase) &&
                    r.AccessLevel == ColumnAccessLevel.Clear &&
                    ConsentResolutionService.RuleAppliesToScopes(r, callerScopes)));

            bool clearancePermits = clearance is null || clearance.Permits(colName);
            if (classified && (!hasExplicitClear || !clearancePermits))
            {
                if (raw == ColumnAccessLevel.Clear && !hasExplicitClear && clearancePermits)
                {
                    implicitMasked.Add(colName);
                }

                map[colName] = ColumnAccessLevel.Mask;
            }
            else
            {
                map[colName] = raw;
            }
        }

        return (map, implicitMasked);
    }

    private static ColumnMaskPolicy? BuildClassificationMaskPolicy(
        IReadOnlyDictionary<string, Autheris.Application.Policy.Classification.ColumnClassificationGrantOutcome>? winners)
    {
        if (winners is null || winners.Count == 0)
        {
            return null;
        }

        Dictionary<string, MaskSpec>? specs = null;
        foreach (var (colName, outcome) in winners)
        {
            if (outcome.MaskSpec is null)
            {
                continue;
            }

            specs ??= new Dictionary<string, MaskSpec>(StringComparer.OrdinalIgnoreCase);
            specs[colName] = outcome.MaskSpec;
        }

        return specs is { Count: > 0 } ? new ColumnMaskPolicy(specs) : null;
    }

    private sealed record SignatureGroup(
        string Key,
        Dictionary<string, ColumnAccessLevel> ColumnAccess,
        HashSet<string> ImplicitMasked,
        List<RowScopeGrant> Grants);
}
