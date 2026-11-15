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
    private bool NeedsClassificationEvaluation(TableAccessQuery query)
    {
        if (_classificationResolver is null || !query.Metadata.Table.IsActive)
        {
            return false;
        }

        return query.Metadata.ColumnClassifications.Count > 0 ||
               query.SupplementalConsents?.Any(c => c.ClassificationAccess.Count > 0) == true ||
               _classificationResolver.HasRepository;
    }

    private async Task<TableAccessDecision> ApplyClassificationAccessAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        CancellationToken ct)
    {
        if (!decision.IsAllowed || !NeedsClassificationEvaluation(query))
        {
            return decision;
        }

        var now = _timeProvider.GetUtcNow();
        IReadOnlyCollection<Sid> subjects = query.AllUserSids is { Count: > 0 }
            ? query.AllUserSids
            : query.GroupSids.Append(query.UserSid).Distinct().ToList();

        var loaded = await _consentRepository.GetActiveConsentsForSubjectsAsync(
            subjects, query.Metadata.Identifier, now, query.Tenant, ct).ConfigureAwait(false);
        var patternConsents = await _consentRepository.GetActivePatternConsentsAsync(
            query.Tenant, now, ct).ConfigureAwait(false);

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
            .Where(c => ConsentResolutionService.IsApplicable(
                            c, query.Metadata.Identifier, now, query.UserSid, query.GroupSids, query.Roles, query.AllUserSids) &&
                        !(isHidden && c.IsPatternGrant && c.Effect == ConsentEffect.Allow))
            .ToList();

        var allowGrants = applicable
            .Where(c => c.Effect == ConsentEffect.Allow && (c.WriteRules.Count == 0 || c.ColumnRules.Count > 0))
            .ToList();

        if (allowGrants.Count == 0)
        {
            return decision;
        }

        var callerScopes = ResolveClassificationCallerScopes(query);
        var (perGrant, combinedWinners) = await _classificationResolver!.ResolveForTableAsync(
            query.Metadata, allowGrants, query.Tenant, callerScopes, ct: ct).ConfigureAwait(false);

        if (perGrant.Values.All(m => m.Count == 0))
        {
            return decision;
        }

        if (_rowScopeCompiler is not null &&
            allowGrants.Count > 1 &&
            allowGrants.Any(c => c.RowFilters.Count > 0 || c.RowScope.Count > 0))
        {
            var perCellResult = await _rowScopeCompiler.CompilePerCellConsentsAsync(
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
                var perCellGrants = await _classificationResolver.BuildGrantAuditEntriesAsync(allowGrants, query.Tenant, ct).ConfigureAwait(false);
                var perCellHiddenText = CollectHiddenCatalogTextColumns(perCellResult.Decision.ColumnAccess, perGrant, combinedWinners);
                return perCellResult.Decision with
                {
                    MaskedColumnsAudit = perCellMasked,
                    GrantClassificationsAudit = perCellGrants,
                    HiddenCatalogTextColumns = perCellHiddenText
                };
            }

            decision = perCellResult.Decision;
        }

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

            if (decision.ColumnAccess.TryGetValue(colName, out var existingLevel) && existingLevel < resolvedLevel)
            {
                resolvedLevel = existingLevel;
            }

            updatedColumns[colName] = resolvedLevel;
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
        return decision with
        {
            ColumnAccess = updatedColumns,
            ColumnMasks = specs.Count > 0 ? new ColumnMaskPolicy(specs) : decision.ColumnMasks,
            MaskedColumnsAudit = maskedAudit,
            GrantClassificationsAudit = await _classificationResolver.BuildGrantAuditEntriesAsync(allowGrants, query.Tenant, ct).ConfigureAwait(false),
            HiddenCatalogTextColumns = CollectHiddenCatalogTextColumns(updatedColumns, perGrant, combinedWinners)
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

    private static HashSet<string> ResolveClassificationCallerScopes(TableAccessQuery query)
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
}
