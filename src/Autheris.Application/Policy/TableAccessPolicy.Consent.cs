namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
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
        Autheris.Application.Catalog.CatalogVersion? catalogVersionAtLoad = null;
        if (_catalogVersionProvider != null)
        {
            catalogVersionAtLoad = await _catalogVersionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
        }

        // MISC-06: a query with supplemental (metric) consents is never answered from, nor written to, the per-user decision cache.
        var useCache = _cacheService != null && query.SupplementalConsents is not { Count: > 0 };
        if (useCache)
        {
            contextHash = IConsentCacheService.ComputeSubjectContextHash(query.GroupSids, query.Roles, scopes);
            if (catalogVersionAtLoad is { } ver)
            {
                contextHash = $"{contextHash}:cat.{ver.StructureVersion}.{ver.ContentVersion}";
            }

            var cached = await _cacheService!.GetCachedDecisionAsync(query.Tenant, query.UserSid, table, contextHash, ct).ConfigureAwait(false);
            if (cached != null && !IsUntypedConsentFilter(cached, query.Metadata))
            {
                return await WithTypedConsentPolicyAsync(cached, query, ct).ConfigureAwait(false);
            }
        }

        // RR-L4-06: epoch snapshot BEFORE loading consents (compare-and-set on cache write)
        var epochAtLoad = useCache ? await _cacheService!.GetEpochSnapshotAsync(table, ct).ConfigureAwait(false) : 0;
        IEnumerable<Sid> userSids = query.AllUserSids != null && query.AllUserSids.Count > 0
            ? query.AllUserSids
            : [query.UserSid];
        var subjects = userSids
            .Concat(query.GroupSids)
            .Distinct()
            .ToList();
        var now = _timeProvider.GetUtcNow();
        // Issue #1043: a batch of decisions reads the consents once (ConsentBatchPrefetch re-applies the per-table rules at `now`).
        // A decision built from prefetched consents is never written to the decision cache: the prefetch was read before the epoch
        // snapshot above, so the compare-and-set (RR-L4-06) could not detect a revoke in between.
        List<Consent> prefetchedConsents = [];
        var fromPrefetch = query.ConsentPrefetch is { } consentPrefetch &&
            consentPrefetch.TryGetActiveConsents(subjects, table, now, query.Tenant, out prefetchedConsents);
        IReadOnlyList<Consent> loadedConsents = fromPrefetch
            ? prefetchedConsents
            : await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, table, now, query.Tenant, ct).ConfigureAwait(false);
        var activeConsents = loadedConsents
            .Where(c => c.TenantId == query.Tenant) // multi-tenancy isolation
            .ToList();
        if (query.SupplementalConsents is { Count: > 0 } supplemental)
        {
            foreach (var extra in supplemental)
            {
                if (extra.TenantId == query.Tenant && activeConsents.All(existing => existing.Id != extra.Id))
                {
                    activeConsents.Add(extra);
                }
            }
        }

        activeConsents = await FilterApplicablePatternConsentsAsync(activeConsents, query.Metadata, now, ct).ConfigureAwait(false);

        var allSids = query.AllUserSids != null && query.AllUserSids.Any(s => s != query.UserSid)
            ? query.AllUserSids
            : null;
        var resolvedScopes = scopes is { Count: > 0 } ? scopes : null;
        var decision = (activeConsents.Any(static c => c.IsPatternGrant) || query.Metadata.Table.ObjectKind == DiscoveryObjectKind.MetricView)
            ? _resolutionService.ResolveAccess(
                query.UserSid,
                query.GroupSids,
                query.Roles,
                table,
                activeConsents,
                query.Metadata.Dialect,
                allSids,
                resolvedScopes,
                query.Metadata)
            : _resolutionService.ResolveAccess(
                query.UserSid,
                query.GroupSids,
                query.Roles,
                table,
                activeConsents,
                query.Metadata.Dialect,
                allSids,
                resolvedScopes);
        if (decision == null)
        {
            return TableAccessDecision.Denied(table, "Access denied: consent resolution returned no decision.");
        }

        decision = ClassifyConsentDenial(decision, activeConsents);
        if (catalogVersionAtLoad is { } catVer)
        {
            decision = decision with { CatalogEpoch = catVer.StructureVersion + catVer.ContentVersion };
        }

        var contributingIds = decision.Grants.Select(g => g.GrantId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contributingAllows = decision.IsAllowed
            ? activeConsents.Where(c => c.Effect == ConsentEffect.Allow && contributingIds.Contains(c.Id.ToString("D"))).ToList()
            : [];
        bool hasGrantRowScopes = contributingAllows.Any(c => c.RowScope.Count > 0);
        bool hasMultiSignatureLegacy = !hasGrantRowScopes &&
            contributingAllows.Count > 1 &&
            contributingAllows.Any(c => c.RowFilters.Count > 0) &&
            HasDistinctGrantColumnRules(contributingAllows) &&
            (_rowScopeCompiler != null || (_rowPolicyFactory != null && query.Metadata.DataSourceType == DataSourceType.Sql && !query.Metadata.IsDelegatedSource));

        if (useCache && !fromPrefetch && !hasGrantRowScopes && !hasMultiSignatureLegacy)
        {
            bool catalogVersionStable = true;
            if (_catalogVersionProvider != null && catalogVersionAtLoad is not null)
            {
                var currentCatalogVersion = await _catalogVersionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
                catalogVersionStable = currentCatalogVersion == catalogVersionAtLoad;
            }

            if (catalogVersionStable)
            {
                var isHigh = ClassificationMetadata.IsTableSensitive(query.Metadata) ||
                             (query.Protection is not null
                                 ? query.Protection.TableClass > Autheris.Domain.Security.ProtectionClass.Internal
                                 : _protectionProvider is null);
                var ttl = ConsentResolutionService.ComputeDecisionCacheTtl(isHigh, activeConsents, _timeProvider.GetUtcNow());
                await _cacheService!.SetCachedDecisionAsync(query.Tenant, query.UserSid, table, decision, ttl, contextHash!, epochAtLoad, ct).ConfigureAwait(false);
            }
        }

        if (query.Metadata.IsDelegatedSource && decision.IsAllowed)
        {
            // Task B3: Coarse gate on delegated sources
            // Defers fine-grained row-level filters and column masks to native source dynamic policies (current_user())
            return decision with { CombinedRowFilterSql = null, RowPolicy = null, MandatoryRowPredicateSql = null };
        }

        if (hasGrantRowScopes)
        {
            return await ApplyCompiledConsentRowScopesAsync(decision, query, activeConsents, contributingAllows, _rowScopeCompiler, _callerAttributes, ct).ConfigureAwait(false);
        }

        if (hasMultiSignatureLegacy)
        {
            var compiler = _rowScopeCompiler ?? new Autheris.Application.Policy.RowScopes.RowScopeCompiler(rowPolicyFactory: _rowPolicyFactory);
            return await ApplyCompiledConsentRowScopesAsync(decision, query, activeConsents, contributingAllows, compiler, _callerAttributes, ct).ConfigureAwait(false);
        }

        return await WithTypedConsentPolicyAsync(decision, query, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// MISC-06: tags a consent-stage denial with its kind from the consents themselves (never from reason text). Only "no ALLOW consent at
    /// all" is <see cref="TableAccessDenialKind.ConsentMissing"/>; a DENY consent is <see cref="TableAccessDenialKind.ConsentDeny"/>; any other
    /// denial (e.g. a restricted-scope or purpose limit despite an ALLOW consent) is final policy.
    /// </summary>
    private async Task<List<Consent>> FilterApplicablePatternConsentsAsync(
        List<Consent> activeConsents,
        TableMetadata metadata,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!activeConsents.Any(static c => c.IsPatternGrant && c.Effect == ConsentEffect.Allow))
        {
            return activeConsents;
        }

        var filtered = new List<Consent>(activeConsents.Count);
        foreach (var consent in activeConsents)
        {
            if (!consent.IsPatternGrant || consent.Effect != ConsentEffect.Allow)
            {
                filtered.Add(consent);
                continue;
            }

            if (metadata.Table.DiscoveryHidden)
            {
                continue;
            }

            if (consent.WorkflowMode == ConsentWorkflowMode.External)
            {
                filtered.Add(consent);
                continue;
            }

            if (consent.ApprovingOwners.Count > 0 &&
                _ownershipRepository != null &&
                await _ownershipRepository.IsAnyApproverAuthorizedForTableAsync(
                    metadata.Identifier, consent.ApprovingOwners, now, ct).ConfigureAwait(false))
            {
                filtered.Add(consent);
            }
        }

        return filtered;
    }

    private static TableAccessDecision ClassifyConsentDenial(TableAccessDecision decision, IReadOnlyList<Consent> activeConsents)
    {
        if (decision.IsAllowed)
        {
            return decision;
        }

        var kind = activeConsents.Any(c => c.Effect == ConsentEffect.Deny)
            ? TableAccessDenialKind.ConsentDeny
            : activeConsents.Any(c => c.Effect == ConsentEffect.Allow)
                ? TableAccessDenialKind.Policy
                : TableAccessDenialKind.ConsentMissing;
        return decision with { DenialKind = kind };
    }

    internal static async Task<TableAccessDecision> ApplyCompiledConsentRowScopesAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        IReadOnlyList<Consent> activeConsents,
        IReadOnlyList<Consent> contributingAllows,
        Autheris.Application.Policy.RowScopes.RowScopeCompiler? compiler,
        Autheris.Application.Identity.Attributes.ICallerAttributeResolver? callerAttributes,
        CancellationToken ct)
    {
        var table = query.Metadata.Identifier;
        if (compiler == null)
        {
            return TableAccessDecision.Denied(table, "Grant row scopes require RowScopeCompiler.") with
            {
                DenialKind = TableAccessDenialKind.Policy,
                Reasons = [new DecisionReason(DecisionStep.DefaultDeny, "ROW_SCOPE_COMPILER_UNAVAILABLE", "RowScope", null)]
            };
        }

        var result = await compiler.CompilePerCellConsentsAsync(
            decision, query, activeConsents, contributingAllows, callerAttributes, ct).ConfigureAwait(false);

        return result.CallerAttributes.Count > 0 && result.Decision.IsAllowed
            ? result.Decision with { CallerAttributes = Merge(result.Decision.CallerAttributes, result.CallerAttributes) }
            : result.Decision;
    }

    private static bool HasDistinctGrantColumnRules(IReadOnlyList<Consent> allows)
    {
        if (allows.Count <= 1)
        {
            return false;
        }

        string? baseline = null;
        foreach (var allow in allows)
        {
            var sig = allow.ColumnRules.Count == 0
                ? "*"
                : string.Join(
                    ';',
                    allow.ColumnRules
                        .OrderBy(static r => r.ColumnName, StringComparer.OrdinalIgnoreCase)
                        .Select(static r => $"{r.ColumnName.ToLowerInvariant()}:{(int)r.AccessLevel}"));
            if (baseline is null)
            {
                baseline = sig;
            }
            else if (!string.Equals(baseline, sig, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static async Task<TableAccessDecision> ApplyAccessProfileRowScopesAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        IReadOnlyList<AccessProfile> profiles,
        Autheris.Application.Policy.RowScopes.RowScopeCompiler? compiler,
        Autheris.Application.Identity.Attributes.ICallerAttributeResolver? callerAttributes,
        CancellationToken ct)
    {
        var entries = profiles.SelectMany(static p => p.RowScope).ToList();
        if (entries.Count == 0)
        {
            return decision;
        }

        var table = query.Metadata.Identifier;
        if (compiler == null)
        {
            return TableAccessDecision.Denied(table, "Access profile row scopes require RowScopeCompiler.") with
            {
                DenialKind = TableAccessDenialKind.Policy,
                Reasons = [new DecisionReason(DecisionStep.DefaultDeny, "ROW_SCOPE_COMPILER_UNAVAILABLE", "AccessProfile", null)]
            };
        }

        var compileRequest = new Autheris.Application.Policy.RowScopes.RowScopeCompileRequest(
            query.Tenant,
            query.UserSid,
            query.GroupSids,
            query.Metadata,
            [new Autheris.Application.Policy.RowScopes.RowScopeGrant("access_profile", ConsentGrantSource.AccessProfile, entries)],
            ObjectKind: query.ObjectKind,
            AllUserSids: query.AllUserSids,
            IncludeHardPolicies: false);

        var outcome = await compiler.CompileGrantsOnlyAsync(compileRequest, ct).ConfigureAwait(false);
        if (outcome.IsDenied)
        {
            return TableAccessDecision.Denied(table, outcome.DenyReason ?? "Denied by access profile row scope.") with
            {
                DenialKind = TableAccessDenialKind.Policy,
                Reasons = [new DecisionReason(outcome.DenyStep ?? DecisionStep.DefaultDeny, outcome.DenyCode ?? "ROW_SCOPE_DENIED", "AccessProfile", null)]
            };
        }

        if (outcome.Predicate == null)
        {
            return decision;
        }

        var predicate = outcome.Predicate;
        IReadOnlyList<CallerAttributeAuditEntry>? audit = null;
        if (outcome.CallerAttributes.Count > 0)
        {
            (predicate, audit) = await BindVirtualFilterAttributesCoreAsync(predicate, outcome.CallerAttributes, query, callerAttributes, ct).ConfigureAwait(false);
        }

        var policy = compiler.BuildRowPolicy(predicate, query.Metadata.Dialect);
        var updated = decision.WithAdditionalRowPolicy(policy);
        return audit is { Count: > 0 }
            ? updated with { CallerAttributes = Merge(updated.CallerAttributes, audit) }
            : updated;
    }

    internal static async Task<TableAccessDecision> ApplyCompiledHardRowPoliciesAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        Autheris.Application.Policy.RowScopes.RowScopeCompiler compiler,
        Autheris.Application.Identity.Attributes.ICallerAttributeResolver? callerAttributes,
        CancellationToken ct)
    {
        var table = query.Metadata.Identifier;
        var outcome = await compiler.CompileHardPoliciesOnlyAsync(
            query.Tenant,
            query.UserSid,
            query.GroupSids,
            query.Metadata,
            query.ObjectKind,
            query.AllUserSids,
            ct: ct).ConfigureAwait(false);

        if (outcome.IsDenied)
        {
            return TableAccessDecision.Denied(table, outcome.DenyReason ?? "Denied by hard row policy.") with
            {
                DenialKind = TableAccessDenialKind.Policy,
                Reasons = [new DecisionReason(outcome.DenyStep ?? DecisionStep.ExplicitDeny, outcome.DenyCode ?? Autheris.Application.Policy.RowScopes.RowScopeCompiler.HardPolicyUncoveredCode, "HardRowPolicy", null)]
            };
        }

        if (outcome.Predicate == null)
        {
            return decision;
        }

        var predicate = outcome.Predicate;
        IReadOnlyList<CallerAttributeAuditEntry>? audit = null;
        if (outcome.CallerAttributes.Count > 0)
        {
            (predicate, audit) = await BindVirtualFilterAttributesCoreAsync(predicate, outcome.CallerAttributes, query, callerAttributes, ct).ConfigureAwait(false);
        }

        var policy = compiler.BuildRowPolicy(predicate, query.Metadata.Dialect);
        var updated = decision.WithMandatoryPredicate(policy, outcome.AppliedHardPolicies, outcome.AppliedHardBindings);
        return audit is { Count: > 0 }
            ? updated with { CallerAttributes = Merge(updated.CallerAttributes, audit) }
            : updated;
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
    internal async Task<TableAccessDecision> WithTypedConsentPolicyAsync(TableAccessDecision decision, TableAccessQuery query, CancellationToken ct) =>
        await BuildTypedConsentPolicyAsync(decision, query, _rowPolicyFactory, _callerAttributes, ct).ConfigureAwait(false);

    internal static async Task<TableAccessDecision> BuildTypedConsentPolicyAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        RowPolicyFactory? rowPolicyFactory,
        Autheris.Application.Identity.Attributes.ICallerAttributeResolver? callerAttributes,
        CancellationToken ct)
    {
        var metadata = query.Metadata;
        if (rowPolicyFactory == null || metadata.DataSourceType != DataSourceType.Sql || metadata.IsDelegatedSource ||
            !decision.IsAllowed || decision.RowPolicy != null || decision.ConsentFilterRecipe == null)
        {
            return decision;
        }

        try
        {
            var (attributes, padding) = await ResolveRecipeAttributesCoreAsync(decision.ConsentFilterRecipe, query, callerAttributes, ct).ConfigureAwait(false);
            var policy = rowPolicyFactory.FromConsentFilters(decision.ConsentFilterRecipe, metadata.Dialect, attributes, padding);
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

    internal static async Task<(Autheris.Application.Identity.Attributes.CallerAttributeSet? Set, Autheris.Application.Identity.Attributes.CallerAttributePadding? Padding)> ResolveRecipeAttributesCoreAsync(
        ConsentRowFilterRecipe recipe,
        TableAccessQuery query,
        Autheris.Application.Identity.Attributes.ICallerAttributeResolver? callerAttributes,
        CancellationToken ct)
    {
        if (callerAttributes is not { IsEnabled: true } resolver)
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
    internal static async Task<(Autheris.Application.Identity.Attributes.CallerAttributeSet? Set, Autheris.Application.Identity.Attributes.CallerAttributePadding? Padding)> ResolveAttributeSetAsync(
        Autheris.Application.Identity.Attributes.ICallerAttributeResolver resolver, IReadOnlyCollection<string> names, TableAccessQuery query, CancellationToken ct)
    {
        var subject = resolver.CreateSubject(query.Principal, query.Tenant);
        var (set, padding) = await Autheris.Application.Identity.Attributes.CallerAttributeRecipes.ResolveAsync(resolver, subject, names, ct).ConfigureAwait(false);
        return (set, padding);
    }

    /// <summary>
    /// 129 (A-1, B-vf): binds the slots of a Virtual Filter predicate for this request. Feature off or anything unresolved: deny all rows
    /// (a Virtual Filter is a restriction ANDed with the consent decision, never a grant).
    /// </summary>
    internal static async Task<(TrinoSqlEngine.Governance.PolicyPredicate Predicate, IReadOnlyList<CallerAttributeAuditEntry>? Audit)> BindVirtualFilterAttributesCoreAsync(
        TrinoSqlEngine.Governance.PolicyPredicate typed,
        IReadOnlyList<string> names,
        TableAccessQuery query,
        Autheris.Application.Identity.Attributes.ICallerAttributeResolver? callerAttributes,
        CancellationToken ct)
    {
        if (callerAttributes is not { IsEnabled: true } resolver)
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
    internal TableAccessDecision ApplyAccessProfiles(TableAccessDecision decision, IReadOnlyList<AccessProfile> profiles, TableMetadata metadata) =>
        ApplyAccessProfilesCore(decision, profiles, metadata, _rowPolicyFactory);

    internal static TableAccessDecision ApplyAccessProfilesCore(
        TableAccessDecision decision,
        IReadOnlyList<AccessProfile> profiles,
        TableMetadata metadata,
        RowPolicyFactory? rowPolicyFactory)
    {
        _ = rowPolicyFactory;
        var table = metadata.Identifier;
        try
        {
            return ApplyProfileMasking(decision, AccessProfileAssignmentRules.EnforcedMode(profiles[0].MaskingMode), metadata);
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
        if (consent.PerCellColumns is Autheris.Application.Policy.RowScopes.PerCellColumnPolicy perCell)
        {
            var (access, rowDeps, sigs, nextPerCell) = perCell.ReprojectForProfile(mode, metadata);
            return consent with
            {
                ColumnAccess = access,
                RowDependentColumns = rowDeps is { Count: > 0 } ? rowDeps : null,
                ColumnSignatures = sigs,
                PerCellColumns = nextPerCell
            };
        }

        var colAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in metadata.Columns)
        {
            var raw = consent.GetColumnAccess(col.ColumnName);
            var effective = consent.GetEffectiveColumnAccess(col.ColumnName, metadata);
            var sensitive = ClassificationMetadata.IsColumnSensitive(metadata, col);
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
    internal Task<IReadOnlyList<AccessProfile>> ResolveActiveAccessProfilesAsync(TableAccessQuery query, CancellationToken ct) =>
        ResolveActiveAccessProfilesCoreAsync(query, _accessProfileCache, _accessProfileRepository, _memoryCache, _options, ct);

    internal static async Task<IReadOnlyList<AccessProfile>> ResolveActiveAccessProfilesCoreAsync(
        TableAccessQuery query,
        Autheris.Application.Policy.Interfaces.IAccessProfileCache? accessProfileCache,
        IAccessProfileRepository? accessProfileRepository,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? memoryCache,
        GatewayOptions? options,
        CancellationToken ct)
    {
        if (accessProfileCache == null && accessProfileRepository == null)
        {
            return [];
        }

        var tenant = query.Tenant;
        if (string.IsNullOrWhiteSpace(query.UserSid.Value))
        {
            return [];
        }

        var users = new List<string> { query.UserSid.Value };
        if (query.AllUserSids != null)
        {
            users.AddRange(query.AllUserSids.Select(s => s.Value));
        }

        var groups = query.GroupSids.Select(g => g.Value).ToList();
        var roles = query.Roles.ToList();

        var cache = accessProfileCache ?? (accessProfileRepository != null
            ? new Autheris.Application.Policy.Services.AccessProfileCache(
                accessProfileRepository,
                clusterState: null,
                memoryCache ?? new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                options != null ? Microsoft.Extensions.Options.Options.Create(options) : null)
            : null);

        IReadOnlyList<AccessProfile>? profiles = cache != null
            ? await cache.GetProfilesForSubjectsAsync(tenant, users, groups, roles, ct).ConfigureAwait(false)
            : await accessProfileRepository!.GetProfilesForSubjectsAsync(tenant, users, groups, roles, ct).ConfigureAwait(false);

        if (profiles == null || profiles.Count == 0)
        {
            return [];
        }

        var table = query.Metadata.Identifier;
        var now = TimeProvider.System.GetUtcNow();
        return AccessProfileAssignmentRules.Order(profiles.Where(p => p.IsActive(now) && p.MatchesTable(table)));
    }
}
