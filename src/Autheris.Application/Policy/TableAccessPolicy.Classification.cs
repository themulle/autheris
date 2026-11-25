namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine.Governance;

public sealed partial class TableAccessPolicy
{
    private static bool NeedsClassificationEvaluation(TableAccessQuery query, ClassificationAccessResolver? resolver)
    {
        if (resolver is null || !query.Metadata.Table.IsActive)
        {
            return false;
        }

        return query.Metadata.ColumnClassifications.Count > 0 ||
               resolver.HasRepository;
    }

    private async Task<List<Consent>> LoadClassificationConsentsAsync(TableAccessQuery query, DateTimeOffset now, CancellationToken ct)
    {
        var subjects = query.GetEffectiveSubjectSids();

        // Issue #1043: from the batch prefetch when it covers this decision, otherwise from the repository.
        IReadOnlyList<Consent> loaded = query.ConsentPrefetch is { } consentPrefetch &&
            consentPrefetch.TryGetActiveConsents(subjects, query.Metadata.Identifier, now, query.Tenant, out var prefetchedLoaded)
                ? prefetchedLoaded
                : await _consentRepository.GetActiveConsentsForSubjectsAsync(
                    subjects, query.Metadata.Identifier, now, query.Tenant, ct).ConfigureAwait(false);
        IReadOnlyList<Consent> patternConsents = query.ConsentPrefetch is { } patternPrefetch &&
            patternPrefetch.TryGetActivePatternConsents(subjects, query.Tenant, now, out var prefetchedPatterns)
                ? prefetchedPatterns
                : await _consentRepository.GetActivePatternConsentsForSubjectsAsync(
                    subjects, query.Tenant, now, ct).ConfigureAwait(false);

        var combinedConsents = new List<Consent>(loaded.Count + patternConsents.Count + (query.SupplementalConsents?.Count ?? 0));
        var seenIds = new HashSet<Guid>();
        foreach (var c in loaded.Concat(patternConsents).Concat(query.SupplementalConsents ?? Array.Empty<Consent>()))
        {
            if (c.Id == Guid.Empty || seenIds.Add(c.Id))
            {
                combinedConsents.Add(c);
            }
        }

        bool isHidden = query.Metadata.Table.DiscoveryHidden;
        var applicable = combinedConsents
            .Where(c => c.TenantId == query.Tenant &&
                        ConsentResolutionService.IsApplicable(
                            c, query.Metadata.Identifier, now, query.UserSid, query.GroupSids, query.Roles, query.AllUserSids) &&
                        !(isHidden && c.IsPatternGrant && c.Effect == ConsentEffect.Allow))
            .ToList();
        return await FilterApplicablePatternConsentsAsync(applicable, query.Metadata, now, query.ConsentPrefetch, ct).ConfigureAwait(false);
    }

    private async Task<TableAccessDecision> ApplyClassificationAccessAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        CancellationToken ct)
    {
        if (!decision.IsAllowed || !NeedsClassificationEvaluation(query, _classificationResolver))
        {
            return decision;
        }

        var applicable = await LoadClassificationConsentsAsync(query, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        var allowGrants = applicable
            .Where(c => c.Effect == ConsentEffect.Allow && (c.WriteRules.Count == 0 || c.ColumnRules.Count > 0))
            .ToList();

        if (allowGrants.Count == 0)
        {
            return decision;
        }

        // Issue #1047: below, only the per-cell compilation of several row-scoped grants can turn the allow into a deny; everything else
        // sets column levels, which an allow-only decision does not carry. The authorization hooks see those levels, so they need them.
        var perCellCanApply = _rowScopeCompiler is not null && allowGrants.Count > 1 && allowGrants.Any(c => c.RowFilters.Count > 0 || c.RowScope.Count > 0);
        if (query.AllowOnly && !perCellCanApply && !AuthorizationHooksActive)
        {
            return decision;
        }

        var callerScopes = Autheris.Domain.Security.TokenAccessScope.GetScopeSet(query.Claims ?? query.Principal?.Claims);
        var (perGrant, combinedWinners) = await _classificationResolver!.ResolveForTableAsync(
            query.Metadata, allowGrants, query.Tenant, callerScopes, columnRowPrefetch: query.ColumnRowPrefetch, unmask: query.Unmask, ct: ct).ConfigureAwait(false);

        if (perGrant.Values.All(m => m.Count == 0))
        {
            return decision;
        }

        if (perCellCanApply)
        {
            var perCellResult = await _rowScopeCompiler!.CompilePerCellConsentsAsync(
                decision, query, applicable, allowGrants, _callerAttributes, ct).ConfigureAwait(false);
            if (!perCellResult.Decision.IsAllowed || perCellResult.Decision.PerCellColumns is not null)
            {
                if (!perCellResult.Decision.IsAllowed)
                {
                    return perCellResult.Decision;
                }

                var perCellMasked = combinedWinners
                    .Where(kv => kv.Value.MaskSpec is not null &&
                                 perCellResult.Decision.ColumnAccess.TryGetValue(kv.Key, out var lvl) &&
                                 lvl is ColumnAccessLevel.Mask or ColumnAccessLevel.RowDependent)
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new MaskedColumnAuditEntry(
                        kv.Key,
                        kv.Value.ClassificationCodes ?? (kv.Value.WinningClassificationCode is not null ? [kv.Value.WinningClassificationCode] : Array.Empty<string>()),
                        kv.Value.WinningMaskFunction.HasValue ? ClassificationMaskFunctions.ToCode(kv.Value.WinningMaskFunction.Value) : "TYPE_SAFE_REDACT",
                        kv.Value.KeyRef))
                    .ToList();
                var perCellHiddenText = CollectHiddenCatalogTextColumns(perCellResult.Decision.ColumnAccess, perGrant, combinedWinners);
                return perCellResult.Decision with
                {
                    MaskedColumnsAudit = perCellMasked,
                    HiddenCatalogTextColumns = perCellHiddenText
                };
            }

            decision = perCellResult.Decision;
        }

        return await ApplyResolvedClassificationColumnsAsync(
            decision, query, applicable, allowGrants, callerScopes, perGrant, combinedWinners, _classificationResolver, preserveOmittedDeny: false, ct).ConfigureAwait(false);
    }

    internal async Task<TableAccessDecision> ApplyWriteClassificationAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        bool hasReadSide,
        CancellationToken ct)
    {
        if (!decision.IsAllowed || !NeedsClassificationEvaluation(query, _classificationResolver))
        {
            return decision;
        }

        var applicable = await LoadClassificationConsentsAsync(query, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        return await ApplyWriteClassificationCoreAsync(
            decision, query, applicable, hasReadSide, _classificationResolver, ct).ConfigureAwait(false);
    }

    internal static async Task<TableAccessDecision> ApplyWriteClassificationCoreAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        IReadOnlyList<Consent> applicable,
        bool hasReadSide,
        ClassificationAccessResolver? classificationResolver,
        CancellationToken ct)
    {
        if (!decision.IsAllowed || !NeedsClassificationEvaluation(query, classificationResolver))
        {
            return decision;
        }

        var allowGrants = applicable
            .Where(c => c.Effect == ConsentEffect.Allow && (c.WriteRules.Count == 0 || c.ColumnRules.Count > 0))
            .ToList();
        if (allowGrants.Count == 0)
        {
            if (hasReadSide)
            {
                return decision;
            }

            allowGrants =
            [
                new Consent
                {
                    Id = Guid.NewGuid(),
                    TenantId = query.Tenant,
                    TableIdentifier = query.Metadata.Identifier,
                    Effect = ConsentEffect.Allow,
                    GranteeType = GranteeType.User,
                    GranteeSid = query.UserSid,
                    ColumnRules = []
                }
            ];
        }

        var callerScopes = Autheris.Domain.Security.TokenAccessScope.GetScopeSet(query.Claims ?? query.Principal?.Claims);
        var (perGrant, combinedWinners) = await classificationResolver!.ResolveForTableAsync(
            query.Metadata, allowGrants, query.Tenant, callerScopes, columnRowPrefetch: query.ColumnRowPrefetch, unmask: query.Unmask, ct: ct).ConfigureAwait(false);

        if (perGrant.Values.All(m => m.Count == 0))
        {
            return decision;
        }

        return await ApplyResolvedClassificationColumnsAsync(
            decision, query, applicable, allowGrants, callerScopes, perGrant, combinedWinners, classificationResolver, preserveOmittedDeny: true, ct).ConfigureAwait(false);
    }

    private static async Task<TableAccessDecision> ApplyResolvedClassificationColumnsAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        IReadOnlyList<Consent> applicable,
        List<Consent> allowGrants,
        IReadOnlySet<string> callerScopes,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, ColumnClassificationGrantOutcome>> perGrant,
        IReadOnlyDictionary<string, ColumnClassificationGrantOutcome> combinedWinners,
        ClassificationAccessResolver classificationResolver,
        bool preserveOmittedDeny,
        CancellationToken ct)
    {

        var updatedColumns = new Dictionary<string, ColumnAccessLevel>(decision.ColumnAccess, StringComparer.OrdinalIgnoreCase);
        var denyGrants = applicable.Where(c => c.Effect == ConsentEffect.Deny).ToList();
        var hardDenied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hardMasked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dc in denyGrants)
        {
            foreach (var cr in dc.ColumnRules)
            {
                if (!ConsentResolutionService.RuleAppliesToScopes(cr, callerScopes))
                {
                    continue;
                }

                if (cr.AccessLevel == ColumnAccessLevel.Deny)
                {
                    hardDenied.Add(cr.ColumnName);
                }
                else if (cr.AccessLevel == ColumnAccessLevel.Mask)
                {
                    hardMasked.Add(cr.ColumnName);
                }
            }
        }

        var unconstrained = allowGrants.Where(c => c.RowFilters.Count == 0 && c.RowScope.Count == 0).ToList();
        var rowGroups = unconstrained.Count > 0
            ? new List<List<Consent>>()
            : allowGrants
                .GroupBy(c => string.Join("|", c.RowFilters.OrderBy(r => r.FilterGroup).ThenBy(r => r.ColumnName).Select(r => $"{r.FilterGroup}:{r.ColumnName}:{r.Operator}:{r.ValueJson}:{r.ValueSource}:{r.UserAttribute}")))
                .Select(g => g.ToList())
                .ToList();

        foreach (var col in query.Metadata.Columns)
        {
            var colName = col.ColumnName;
            if (!combinedWinners.TryGetValue(colName, out _) &&
                !perGrant.Values.Any(g => g.ContainsKey(colName)))
            {
                continue;
            }

            if (hardDenied.Contains(colName))
            {
                updatedColumns[colName] = ColumnAccessLevel.Deny;
                continue;
            }

            ColumnAccessLevel resolvedLevel;
            if (unconstrained.Count > 0)
            {
                resolvedLevel = unconstrained.Max(g => GetAdjustedGrantLevel(g, colName, callerScopes, perGrant));
            }
            else
            {
                resolvedLevel = ColumnAccessLevel.Clear;
                foreach (var group in rowGroups)
                {
                    var groupMax = group.Max(g => GetAdjustedGrantLevel(g, colName, callerScopes, perGrant));
                    if (groupMax < resolvedLevel)
                    {
                        resolvedLevel = groupMax;
                    }
                }
            }

            if (hardMasked.Contains(colName) && resolvedLevel == ColumnAccessLevel.Clear)
            {
                resolvedLevel = ColumnAccessLevel.Mask;
            }

            if (preserveOmittedDeny)
            {
                var existingLevel = decision.GetColumnAccess(colName);
                if (existingLevel < resolvedLevel)
                {
                    resolvedLevel = existingLevel;
                }

                if (decision.ColumnAccess.ContainsKey(colName) || resolvedLevel != ColumnAccessLevel.Clear)
                {
                    updatedColumns[colName] = resolvedLevel;
                }
            }
            else
            {
                if (decision.ColumnAccess.TryGetValue(colName, out var existingLevel) && existingLevel < resolvedLevel)
                {
                    resolvedLevel = existingLevel;
                }

                updatedColumns[colName] = resolvedLevel;
            }
        }

        var specs = new Dictionary<string, MaskSpec>(StringComparer.OrdinalIgnoreCase);
        if (decision.ColumnMasks is ColumnMaskPolicy existingPolicy)
        {
            foreach (var (k, v) in existingPolicy.Specs)
            {
                specs[k] = v;
            }
        }

        var maskedAudit = new List<MaskedColumnAuditEntry>();
        foreach (var (colName, winner) in combinedWinners.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (winner.MaskSpec is null || !updatedColumns.TryGetValue(colName, out var lvl) || lvl is not (ColumnAccessLevel.Mask or ColumnAccessLevel.RowDependent)) continue;
            specs[colName] = winner.MaskSpec;
            var fnCode = winner.WinningMaskFunction.HasValue ? ClassificationMaskFunctions.ToCode(winner.WinningMaskFunction.Value) : "TYPE_SAFE_REDACT";
            var codes = winner.ClassificationCodes ?? (winner.WinningClassificationCode is not null ? [winner.WinningClassificationCode] : Array.Empty<string>());
            maskedAudit.Add(new MaskedColumnAuditEntry(colName, codes, fnCode, winner.KeyRef));
        }
        var hiddenText = CollectHiddenCatalogTextColumns(updatedColumns, perGrant, combinedWinners);
        if (decision.ScopeClearance is { } scopeClearance &&
            updatedColumns.Any(kv => kv.Value == ColumnAccessLevel.Clear && !scopeClearance.Permits(kv.Key) && ClassificationMetadata.IsColumnSensitive(query.Metadata, kv.Key)))
        {
            var ruleHidden = await ClassificationAccessResolver.ResolveIndexingHiddenTextColumnsAsync(
                query.Metadata, classificationResolver.Repository, ct).ConfigureAwait(false);
            foreach (var (colName, lvl) in updatedColumns)
            {
                if (lvl == ColumnAccessLevel.Clear &&
                    !scopeClearance.Permits(colName) &&
                    ClassificationMetadata.IsColumnSensitive(query.Metadata, colName) &&
                    ruleHidden.Contains(colName))
                {
                    var mutable = hiddenText as HashSet<string> ?? new HashSet<string>(hiddenText ?? (IEnumerable<string>)[], StringComparer.OrdinalIgnoreCase);
                    mutable.Add(colName);
                    hiddenText = mutable;
                }
            }
        }

        return decision with
        {
            ColumnAccess = updatedColumns,
            ColumnMasks = specs.Count > 0 ? new ColumnMaskPolicy(specs) : decision.ColumnMasks,
            MaskedColumnsAudit = maskedAudit,
            HiddenCatalogTextColumns = hiddenText
        };
    }

    private static IReadOnlySet<string>? CollectHiddenCatalogTextColumns(
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, ColumnClassificationGrantOutcome>> perGrant,
        IReadOnlyDictionary<string, ColumnClassificationGrantOutcome> combinedWinners)
    {
        HashSet<string>? hidden = null;
        foreach (var (colName, lvl) in columnAccess)
        {
            if (lvl == ColumnAccessLevel.Clear) continue;
            bool isHidden = (combinedWinners.TryGetValue(colName, out var winner) && winner.CatalogText == CatalogTextVisibility.Hidden) ||
                            perGrant.Values.Any(g => g.TryGetValue(colName, out var o) && o.AccessLevel != ColumnAccessLevel.Clear && o.CatalogText == CatalogTextVisibility.Hidden);
            if (isHidden)
            {
                (hidden ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(colName);
            }
        }
        return hidden;
    }

    private static ColumnAccessLevel GetAdjustedGrantLevel(
        Consent grant,
        string colName,
        IReadOnlySet<string> callerScopes,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, ColumnClassificationGrantOutcome>> perGrant)
    {
        var raw = ConsentResolutionService.ResolveConsentColumnLevel(grant, colName, callerScopes);
        if (raw == ColumnAccessLevel.Deny)
        {
            return ColumnAccessLevel.Deny;
        }

        if (perGrant.TryGetValue(grant.Id, out var map) && map.TryGetValue(colName, out var outcome))
        {
            if (outcome.AccessLevel == ColumnAccessLevel.Deny)
            {
                return ColumnAccessLevel.Deny;
            }

            if (outcome.AccessLevel == ColumnAccessLevel.Mask && raw == ColumnAccessLevel.Clear)
            {
                return ColumnAccessLevel.Mask;
            }
        }

        return raw;
    }
}
