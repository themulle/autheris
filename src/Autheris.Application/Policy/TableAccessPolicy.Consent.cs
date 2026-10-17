namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed partial class TableAccessPolicy
{
    private async Task<bool> IsRebacDeniedAsync(TableAccessQuery query, CancellationToken ct)
    {
        if (_options?.IsRebacBypassed == true)
        {
            return false;
        }

        var enforced = query.Rebac switch
        {
            RebacEnforcement.WhenEnabled => _rebacEvaluator is { IsEnabled: true },
            _ => RebacTableGate.IsEnforcedOnQueryPaths(_options)
        };

        return enforced &&
               !await RebacTableGate.IsAllowedAsync(_rebacEvaluator, query.Tenant, query.UserSid, query.Metadata.Identifier, ct).ConfigureAwait(false);
    }

    private async Task<TableAccessDecision> ResolveConsentAsync(TableAccessQuery query, CancellationToken ct)
    {
        var table = query.Metadata.Identifier;
        var scopes = Autheris.Domain.Security.TokenAccessScope.GetScopeSet(query.Claims);
        string? contextHash = null;
        if (_cacheService != null)
        {
            contextHash = IConsentCacheService.ComputeSubjectContextHash(query.GroupSids, query.Roles, scopes);
            var cached = await _cacheService.GetCachedDecisionAsync(query.Tenant, query.UserSid, table, contextHash, ct).ConfigureAwait(false);
            if (cached != null && !IsUntypedConsentFilter(cached, query.Metadata))
            {
                return await WithTypedConsentPolicyAsync(cached, query, ct).ConfigureAwait(false);
            }
        }

        // RR-L4-06: epoch snapshot BEFORE loading consents (compare-and-set on cache write)
        var epochAtLoad = _cacheService != null ? await _cacheService.GetEpochSnapshotAsync(table, ct).ConfigureAwait(false) : 0;
        IEnumerable<Sid> userSids = query.AllUserSids != null && query.AllUserSids.Count > 0
            ? query.AllUserSids
            : [query.UserSid];
        var subjects = userSids
            .Concat(query.GroupSids)
            .Distinct()
            .ToList();
        var activeConsents = (await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, table, DateTimeOffset.UtcNow, query.Tenant, ct).ConfigureAwait(false))
            .Where(c => c.TenantId == query.Tenant) // multi-tenancy isolation
            .ToList();

        var allSids = query.AllUserSids != null && query.AllUserSids.Any(s => s != query.UserSid)
            ? query.AllUserSids
            : null;
        var decision = _resolutionService.ResolveAccess(
            query.UserSid,
            query.GroupSids,
            query.Roles,
            table,
            activeConsents,
            query.Metadata.Dialect,
            allSids,
            scopes is { Count: > 0 } ? scopes : null);
        if (decision == null)
        {
            return TableAccessDecision.Denied(table, "Access denied: consent resolution returned no decision.");
        }

        if (_cacheService != null)
        {
            // SEC: TTL bounded by the earliest consent ValidTo; shorter for highly sensitive tables.
            var ttl = ConsentResolutionService.ComputeDecisionCacheTtl(query.Metadata.Table.IsHighlySensitive, activeConsents, DateTimeOffset.UtcNow);
            await _cacheService.SetCachedDecisionAsync(query.Tenant, query.UserSid, table, decision, ttl, contextHash!, epochAtLoad, ct).ConfigureAwait(false);
        }

        return await WithTypedConsentPolicyAsync(decision, query, ct).ConfigureAwait(false);
    }

    /// <summary>The compiler path needs the typed form only for SQL sources; the other sources keep the legacy text (no marker consumers there).</summary>
    private bool WantsTypedConsentPolicy(TableMetadata metadata) =>
        _rowPolicyFactory != null && metadata.DataSourceType == DataSourceType.Sql;

    /// <summary>
    /// WP-X0l: a cached decision that carries a consent row filter as text only (an entry written before the recipe existed, or one whose
    /// recipe did not survive the L2 round trip) cannot be typed. It is treated as a miss, so the consents are resolved again.
    /// </summary>
    private bool IsUntypedConsentFilter(TableAccessDecision cached, TableMetadata metadata) =>
        WantsTypedConsentPolicy(metadata) && cached.IsAllowed && cached.ConsentFilterRecipe == null &&
        !string.IsNullOrWhiteSpace(cached.CombinedRowFilterSql);

    /// <summary>
    /// WP-X0l: replaces the legacy consent text by the typed row policy built from the same filter inputs (the decision's
    /// <see cref="ConsentRowFilterRecipe"/>). The text becomes the projection of the policy and the values become bound parameters. A
    /// filter that cannot be typed leaves the decision string-only, which the compiler path denies.
    /// </summary>
    private async Task<TableAccessDecision> WithTypedConsentPolicyAsync(TableAccessDecision decision, TableAccessQuery query, CancellationToken ct)
    {
        var metadata = query.Metadata;
        if (!WantsTypedConsentPolicy(metadata) || !decision.IsAllowed || decision.RowPolicy != null || decision.ConsentFilterRecipe == null)
        {
            return decision;
        }

        try
        {
            // REQ-GOV-129 (A-1, B-consent): values are bound here, per request, into the typed policy; the cached decision stays value-free.
            var (attributes, padding) = await ResolveRecipeAttributesAsync(decision.ConsentFilterRecipe, query, ct).ConfigureAwait(false);
            var policy = _rowPolicyFactory!.FromConsentFilters(decision.ConsentFilterRecipe, metadata.Dialect, attributes, padding);
            return policy == null
                ? decision
                : (decision with { CombinedRowFilterSql = null, RowFilterParameters = null }).WithAdditionalRowPolicy(policy);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return decision;
        }
    }

    /// <summary>
    /// The caller attributes the recipe names, resolved once for this request (null when the recipe names none, the feature is off or no
    /// resolver is registered: the builder then keeps the constants). A statement-wide bind budget guards the expansion (BC-129-09): over the
    /// budget every attribute of the recipe is <c>OverCardinality</c>, which is the same constant as <c>Missing</c>.
    /// </summary>
    private async Task<(Autheris.Application.Identity.Attributes.CallerAttributeSet? Set, Autheris.Application.Identity.Attributes.CallerAttributePadding? Padding)> ResolveRecipeAttributesAsync(
        ConsentRowFilterRecipe recipe, TableAccessQuery query, CancellationToken ct)
    {
        if (_callerAttributes is not { IsEnabled: true } resolver)
        {
            return (null, null);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filter in recipe.Allow.Concat(recipe.Deny).SelectMany(static f => f))
        {
            if (string.Equals(filter.ValueSource, "USER_ATTRIBUTE", StringComparison.OrdinalIgnoreCase) &&
                filter.FilterType == RowFilterType.SimpleColumnPredicate &&
                CallerAttributeDefinition.IsValidName(filter.UserAttribute))
            {
                names.Add(filter.UserAttribute!);
            }
        }

        if (names.Count == 0)
        {
            return (null, null);
        }

        var padding = resolver.Padding;
        var subject = resolver.CreateSubject(query.Principal, query.Tenant);
        var set = await resolver.ResolveAsync(subject, names, ct).ConfigureAwait(false);

        int items = 0;
        foreach (var name in names)
        {
            if (set.TryGet(name, out var result) && result.Outcome == Autheris.Application.Identity.Attributes.CallerAttributeOutcome.Resolved && result.Value is { } value)
            {
                items += padding.PaddedCount(value.Count);
            }
        }

        if (items > 0 && !subject.TryReserveBinds(items, resolver.BindReserve))
        {
            set = new Autheris.Application.Identity.Attributes.CallerAttributeSet(
                names.ToDictionary(n => n, _ => Autheris.Application.Identity.Attributes.CallerAttributeResult.OverCardinality(), StringComparer.Ordinal));
        }

        return (set, padding);
    }

    /// <summary>
    /// SR-M-07: applies an access profile to an already allowed consent decision. <see cref="MaskingPolicyMode.Unmasked"/> relaxes
    /// masked columns to Clear (a Deny stays a Deny); Default and Strict can only lower the level of sensitive columns. The profile
    /// row filter is AND-ed with the consent row filter.
    /// </summary>
    private static TableAccessDecision ApplyProfileToDecision(TableAccessDecision consent, AccessProfile profile, TableMetadata metadata, IRowPolicy? rowPolicy)
    {
        var colAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in metadata.Columns)
        {
            var raw = consent.GetColumnAccess(col.ColumnName);
            var effective = consent.GetEffectiveColumnAccess(col.ColumnName, metadata);
            var sensitive = col.IsSensitive || metadata.ColumnMaskingRules.ContainsKey(col.ColumnName);
            ColumnAccessLevel level;
            if (raw == ColumnAccessLevel.Deny)
            {
                level = ColumnAccessLevel.Deny;
            }
            else
            {
                level = profile.MaskingMode switch
                {
                    MaskingPolicyMode.Unmasked => effective == ColumnAccessLevel.Mask ? ColumnAccessLevel.Clear : raw,
                    MaskingPolicyMode.Default => sensitive && raw == ColumnAccessLevel.Clear && !consent.HasExplicitClear(col.ColumnName) ? ColumnAccessLevel.Mask : raw,
                    MaskingPolicyMode.Strict => sensitive ? ColumnAccessLevel.Deny : raw,
                    _ => raw
                };
            }

            colAccess[col.ColumnName] = level;
        }

        var restricted = consent with { ColumnAccess = colAccess };
        if (rowPolicy != null)
        {
            return restricted.WithAdditionalRowPolicy(rowPolicy);
        }

        return string.IsNullOrWhiteSpace(profile.RowFilterPredicate)
            ? restricted
            : restricted.WithAdditionalRowFilter(profile.RowFilterPredicate);
    }

    private async Task<AccessProfile?> ResolveActiveAccessProfileAsync(TableAccessQuery query, CancellationToken ct)
    {
        if (_accessProfileCache == null && _accessProfileRepository == null)
        {
            return null;
        }

        var tenant = query.Tenant;
        if (string.IsNullOrWhiteSpace(query.UserSid.Value))
        {
            return null;
        }

        // WP-SC-03: ONE lookup with every identity of the caller (SIDs, aliases), its groups and its roles. Group and role mappings only match through
        // this call; the profile of the first SID must not hide the profiles of the other identities.
        var users = new List<string> { query.UserSid.Value };
        if (query.AllUserSids != null)
        {
            users.AddRange(query.AllUserSids.Select(s => s.Value));
        }

        var groups = query.GroupSids.Select(g => g.Value).ToList();
        var roles = query.Roles.ToList();

        IReadOnlyList<AccessProfile>? profiles = _accessProfileCache != null
            ? await _accessProfileCache.GetProfilesForSubjectsAsync(tenant, users, groups, roles, ct).ConfigureAwait(false)
            : await _accessProfileRepository!.GetProfilesForSubjectsAsync(tenant, users, groups, roles, ct).ConfigureAwait(false);

        if (profiles == null || profiles.Count == 0)
        {
            return null;
        }

        // Most restrictive wins across ALL matched profiles (Strict, Default, Unmasked); the cached entry is re-checked for expiry on every use.
        var table = query.Metadata.Identifier;
        var now = DateTimeOffset.UtcNow;
        return AccessProfileAssignmentRules
            .Order(profiles.Where(p => p.IsActive(now) && p.MatchesTable(table)))
            .FirstOrDefault();
    }
}
