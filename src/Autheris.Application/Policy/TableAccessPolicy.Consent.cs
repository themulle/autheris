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

        if (query.Metadata.IsDelegatedSource && decision.IsAllowed)
        {
            // Task B3 (PLAN-SCALE-5000-WAVE2 / SEC-SC-13): Coarse gate on delegated sources
            // Defers fine-grained row-level filters and column masks to native source dynamic policies (current_user())
            return decision with { CombinedRowFilterSql = null, RowPolicy = null, MandatoryRowPredicateSql = null };
        }

        return await WithTypedConsentPolicyAsync(decision, query, ct).ConfigureAwait(false);
    }

    /// <summary>The compiler path needs the typed form only for SQL sources; the other sources keep the legacy text (no marker consumers there).</summary>
    private bool WantsTypedConsentPolicy(TableMetadata metadata) =>
        _rowPolicyFactory != null && metadata.DataSourceType == DataSourceType.Sql && !metadata.IsDelegatedSource;

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
            var typed = policy == null
                ? decision
                : (decision with { CombinedRowFilterSql = null, RowFilterParameters = null }).WithAdditionalRowPolicy(policy);
            return attributes is { IsEmpty: false } ? typed with { CallerAttributes = Merge(typed.CallerAttributes, attributes.AuditEntries()) } : typed;
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

        var names = Autheris.Application.Identity.Attributes.CallerAttributeRecipes.Names(recipe);
        if (names.Count == 0)
        {
            return (null, null);
        }

        return await ResolveAttributeSetAsync(resolver, names, query, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves <paramref name="names"/> for the caller of <paramref name="query"/> once, under the request's bind reserve (BC-129-09).</summary>
    private static async Task<(Autheris.Application.Identity.Attributes.CallerAttributeSet? Set, Autheris.Application.Identity.Attributes.CallerAttributePadding? Padding)> ResolveAttributeSetAsync(
        Autheris.Application.Identity.Attributes.ICallerAttributeResolver resolver, IReadOnlyCollection<string> names, TableAccessQuery query, CancellationToken ct)
    {
        var subject = resolver.CreateSubject(query.Principal, query.Tenant);
        var (set, padding) = await Autheris.Application.Identity.Attributes.CallerAttributeRecipes.ResolveAsync(resolver, subject, names, ct).ConfigureAwait(false);
        return (set, padding);
    }

    /// <summary>
    /// REQ-GOV-129 (A-1, B-vf): binds the slots of a Virtual Filter predicate for this request. Feature off or anything unresolved: deny all rows
    /// (a Virtual Filter is a restriction ANDed with the consent decision, never a grant).
    /// </summary>
    private async Task<(TrinoSqlEngine.Governance.PolicyPredicate Predicate, IReadOnlyList<CallerAttributeAuditEntry>? Audit)> BindVirtualFilterAttributesAsync(
        TrinoSqlEngine.Governance.PolicyPredicate typed, IReadOnlyList<string> names, TableAccessQuery query, CancellationToken ct)
    {
        if (_callerAttributes is not { IsEnabled: true } resolver)
        {
            return (TrinoSqlEngine.Governance.PolicyPredicate.DenyAll, null);
        }

        var (set, padding) = await ResolveAttributeSetAsync(resolver, names, query, ct).ConfigureAwait(false);
        return (Autheris.Application.Identity.Attributes.CallerAttributeBinder.Bind(typed, set!, padding!), set!.AuditEntries());
    }

    private static IReadOnlyList<CallerAttributeAuditEntry> Merge(IReadOnlyList<CallerAttributeAuditEntry>? existing, IReadOnlyList<CallerAttributeAuditEntry> added) =>
        existing is null or { Count: 0 } ? added : [.. existing, .. added.Where(a => !existing.Contains(a))];

    /// <summary>
    /// SR-M-07: applies the matched access profiles (<paramref name="profiles"/>, most restrictive first) to an already allowed consent decision.
    /// The masking mode is the most restrictive one among them; the row filters of ALL of them are AND-ed with the consent row filter, so one
    /// profile never drops the row restriction of another. An admin predicate that cannot be parsed into a typed policy, or conflicting row
    /// filter parameters, deny the table (fail closed); the denial reason never echoes the predicate text.
    /// </summary>
    private TableAccessDecision ApplyAccessProfiles(TableAccessDecision decision, IReadOnlyList<AccessProfile> profiles, TableMetadata metadata)
    {
        var table = metadata.Identifier;
        var predicates = profiles
            .Select(p => p.RowFilterPredicate)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rowPolicies = new List<IRowPolicy>();
        if (_rowPolicyFactory is { CanParseAdminPredicates: true })
        {
            // WP-X0e: each admin-authored predicate is parsed on its own into a typed policy (never concatenated as text first).
            try
            {
                foreach (var predicate in predicates)
                {
                    rowPolicies.Add(_rowPolicyFactory.FromAccessProfile(predicate, metadata));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException)
            {
                return TableAccessDecision.Denied(table, "Access profile row filter is invalid (fail-closed).");
            }
        }

        try
        {
            var restricted = ApplyProfileMasking(decision, AccessProfileAssignmentRules.EnforcedMode(profiles[0].MaskingMode), metadata);
            if (rowPolicies.Count > 0)
            {
                foreach (var policy in rowPolicies)
                {
                    restricted = restricted.WithAdditionalRowPolicy(policy);
                }

                return restricted;
            }

            foreach (var predicate in predicates)
            {
                restricted = restricted.WithAdditionalRowFilter(predicate);
            }

            return restricted;
        }
        catch (System.Security.SecurityException)
        {
            return TableAccessDecision.Denied(table, "Conflicting row-level security parameters.");
        }
    }

    /// <summary>
    /// SR-M-07: the column levels of an access profile on top of an allowed consent decision. <see cref="MaskingPolicyMode.Unmasked"/> lifts
    /// only the catalog default mask (an implicit or explicit Clear that the catalog masks because the column is sensitive); a Mask set by the
    /// consents themselves (Deny-consent masks, explicit Mask levels, scope rules that tighten to Mask) stays a Mask and a Deny stays a Deny.
    /// Default and Strict can only lower the level of sensitive columns. <paramref name="mode"/> must be a defined value
    /// (<see cref="AccessProfileAssignmentRules.EnforcedMode"/>); anything else is handled like Strict.
    /// </summary>
    private static TableAccessDecision ApplyProfileMasking(TableAccessDecision consent, MaskingPolicyMode mode, TableMetadata metadata)
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
                level = mode switch
                {
                    MaskingPolicyMode.Unmasked => raw == ColumnAccessLevel.Clear && effective == ColumnAccessLevel.Mask ? ColumnAccessLevel.Clear : raw,
                    MaskingPolicyMode.Default => sensitive && raw == ColumnAccessLevel.Clear && !consent.HasExplicitClear(col.ColumnName) ? ColumnAccessLevel.Mask : raw,
                    MaskingPolicyMode.Strict => sensitive ? ColumnAccessLevel.Deny : raw,
                    // Fail closed: an unknown mode must never turn an implicit Clear into an explicit one (which would unmask a sensitive column).
                    _ => sensitive ? ColumnAccessLevel.Deny : raw
                };
            }

            colAccess[col.ColumnName] = level;
        }

        return consent with { ColumnAccess = colAccess };
    }

    /// <summary>
    /// The active profiles of the caller that match the table, most restrictive first (<see cref="AccessProfileAssignmentRules.Order"/>);
    /// empty when none applies.
    /// </summary>
    private async Task<IReadOnlyList<AccessProfile>> ResolveActiveAccessProfilesAsync(TableAccessQuery query, CancellationToken ct)
    {
        if (_accessProfileCache == null && _accessProfileRepository == null)
        {
            return [];
        }

        var tenant = query.Tenant;
        if (string.IsNullOrWhiteSpace(query.UserSid.Value))
        {
            return [];
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
            return [];
        }

        // The most restrictive masking mode wins across ALL matched profiles (Strict, Default, Unmasked) and every row filter applies; the cached
        // entry is re-checked for expiry on every use.
        var table = query.Metadata.Identifier;
        var now = DateTimeOffset.UtcNow;
        return AccessProfileAssignmentRules.Order(profiles.Where(p => p.IsActive(now) && p.MatchesTable(table)));
    }
}
