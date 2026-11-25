namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed partial class DiscoveryService
{
    /// <summary>
    /// Issues #798 / #804: the caller's access state of <paramref name="tables"/>, set-based and memoized per caller scope (like
    /// <see cref="LoadScopedCatalogAsync"/>), so a paging loop (the REST object list reads 200-row pages) decides each table once per request.
    /// One read of the caller's active consents gives the grant candidates (<see cref="CallerAccessMemo.IsCandidate"/>); only candidates not
    /// decided yet go to the access decision point, so a caller with three grants in a catalog of 3,000 tables costs three decisions, not 3,000.
    /// The pending and expired sets are the ones the per-object state uses, read once and only when <paramref name="wanted"/> needs them. Each
    /// state follows <see cref="DiscoveryAccessMapper.AccessStateOf"/>, the rule of the per-object <see cref="DiscoveryObject.Access"/>.
    /// </summary>
    private async Task<CallerAccessView> LoadAccessViewAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        IReadOnlyList<TableMetadata> tables,
        DiscoveryAccessState wanted,
        CancellationToken ct)
    {
        var subject = SecurityPrincipalContext.FromPrincipal(caller.Principal!, caller.Tenant);
        var memo = await LoadAccessMemoAsync(caller, sid, subject, scopeKey, ct).ConfigureAwait(false);

        List<TableMetadata>? undecided = null;
        lock (_memoLock)
        {
            foreach (var table in tables)
            {
                if (memo.IsCandidate(table) && !memo.TryGetAllowed(table.Identifier, out _))
                {
                    (undecided ??= []).Add(table);
                }
            }
        }

        if (undecided is not null)
        {
            // Issue #1047: the filter only needs the allow bit; page items are decided in full by ResolveObjectsBatchAsync.
            var allowed = await _decisionPoint!.DecideAllowedManyAsync(subject, undecided, ct).ConfigureAwait(false);
            if (allowed.Count != undecided.Count)
            {
                throw new InvalidOperationException("AccessDecisionPoint returned mismatched decision count.");
            }

            lock (_memoLock)
            {
                for (var i = 0; i < undecided.Count; i++)
                {
                    memo.Allowed[undecided[i].Identifier] = allowed[i];
                }
            }
        }

        if (wanted != DiscoveryAccessState.Granted)
        {
            memo.Pending ??= await LoadPendingRequestTablesAsync(sid, caller.Tenant, ct).ConfigureAwait(false);
            memo.Expired ??= await LoadRecentlyExpiredTablesAsync(sid, caller.Tenant, ct).ConfigureAwait(false);
        }

        var granted = new HashSet<TableIdentifier>();
        lock (_memoLock)
        {
            foreach (var table in tables)
            {
                if (memo.TryGetAllowed(table.Identifier, out var allowed) && allowed)
                {
                    granted.Add(table.Identifier);
                }
            }
        }

        return new CallerAccessView(granted, memo.Pending ?? EmptyTableSet, memo.Expired ?? EmptyTableSet);
    }

    /// <summary>
    /// The grant sources of the caller, read once per scope: every table is a candidate when consent is bypassed, otherwise the tables an
    /// active Allow read consent of the caller (user SIDs and group SIDs, caller tenant) targets, directly or by a pattern grant (never a hidden
    /// table by pattern). Zero trust: the consent resolver denies a read without such a consent, and an access profile never creates an allow
    /// (SR-M-07), so no other table can be granted. The decision point still decides every candidate; the set only spares it the tables it
    /// cannot allow.
    /// </summary>
    private async Task<CallerAccessMemo> LoadAccessMemoAsync(
        CatalogCaller caller,
        Sid sid,
        SecurityPrincipalContext subject,
        ScopeMemoKey scopeKey,
        CancellationToken ct)
    {
        lock (_memoLock)
        {
            if (_accessMemo.TryGetValue(scopeKey, out var cached))
            {
                return cached;
            }
        }

        var memo = AccessViewCacheKey(caller, subject) is { } cacheKey
            ? await LoadCachedAccessMemoAsync(caller, sid, subject, scopeKey, cacheKey, ct).ConfigureAwait(false)
            : new CallerAccessMemo(await LoadGrantCandidatesAsync(caller, subject, ct).ConfigureAwait(false));

        lock (_memoLock)
        {
            return _accessMemo.TryAdd(scopeKey, memo) ? memo : _accessMemo[scopeKey];
        }
    }

    private Task<ReadGrantCandidates> LoadGrantCandidatesAsync(CatalogCaller caller, SecurityPrincipalContext subject, CancellationToken ct)
    {
        if (_gatewayOptions.IsConsentBypassed)
        {
            return Task.FromResult(ReadGrantCandidates.All);
        }

        var subjects = caller.Principal!.GetAllUserSids();
        subjects.Add(subject.UserSid);
        subjects.UnionWith(subject.GroupSids);
        return ReadGrantCandidates.LoadAsync(_consentRepository!, subjects, _timeProvider.GetUtcNow(), caller.Tenant, ct);
    }

    /// <summary>
    /// Issue #1044: the caller's access memo from the cross-request view cache. A hit is used as is. When tables were invalidated since
    /// the entry's load, the grant candidates are read again (so a new grant counts at once) and only the invalidated and the new
    /// candidates are decided. A miss decides every candidate of the caller's scoped catalog. The result is stored under compare-and-set
    /// with the stamp read before the load; requests of the same key wait for one load (striped gate).
    /// </summary>
    private async Task<CallerAccessMemo> LoadCachedAccessMemoAsync(
        CatalogCaller caller,
        Sid sid,
        SecurityPrincipalContext subject,
        ScopeMemoKey scopeKey,
        string cacheKey,
        CancellationToken ct)
    {
        var cache = _accessViewCache!;

        // A current entry is used without waiting for anyone; entries with omitted transient failures trigger re-check (#1063 §4c).
        if (await cache.ReadStampAsync(caller.Tenant, ct).ConfigureAwait(false) is { } early &&
            cache.TryGet(cacheKey, early) is { Stale.Count: 0, Entry.HasTransientOmissions: false } current)
        {
            return CallerAccessMemo.FromView(current.Entry.Candidates, current.Entry.Allowed, stale: null, cacheKey);
        }

        var gate = cache.GateFor(cacheKey);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await cache.ReadStampAsync(caller.Tenant, ct).ConfigureAwait(false) is not { } stamp)
            {
                return new CallerAccessMemo(await LoadGrantCandidatesAsync(caller, subject, ct).ConfigureAwait(false));
            }

            var hit = cache.TryGet(cacheKey, stamp);
            if (hit is { Stale.Count: 0, Entry.HasTransientOmissions: false } fresh)
            {
                return CallerAccessMemo.FromView(fresh.Entry.Candidates, fresh.Entry.Allowed, stale: null, cacheKey);
            }

            var candidates = hit is { Stale.Count: 0 } transientHit
                ? transientHit.Entry.Candidates
                : await LoadGrantCandidatesAsync(caller, subject, ct).ConfigureAwait(false);
            var memo = hit is { } revalidate
                ? CallerAccessMemo.FromView(candidates, revalidate.Entry.Allowed, revalidate.Stale, cacheKey)
                : CallerAccessMemo.FromView(candidates, FrozenDictionary<TableIdentifier, bool>.Empty, stale: null, cacheKey);

            var scoped = await LoadScopedCatalogAsync(caller, sid, scopeKey, ct).ConfigureAwait(false);
            var undecided = scoped.VisibleTables.Where(t => memo.IsCandidate(t) && !memo.TryGetAllowed(t.Identifier, out _)).ToList();
            HashSet<TableIdentifier>? transientOmissions = null;
            if (undecided.Count > 0)
            {
                var decisions = await _decisionPoint!.DecideAllowedManyWithTransientAsync(subject, undecided, ct).ConfigureAwait(false);
                if (decisions.Count != undecided.Count)
                {
                    throw new InvalidOperationException("AccessDecisionPoint returned mismatched decision count.");
                }

                for (var i = 0; i < undecided.Count; i++)
                {
                    var d = decisions[i];
                    memo.Allowed[undecided[i].Identifier] = d.IsAllowed;
                    if (!d.IsAllowed && d.IsTransientFailure)
                    {
                        (transientOmissions ??= []).Add(undecided[i].Identifier);
                    }
                }
            }

            var notAfter = Min(hit?.Entry.NotAfter ?? cache.Time.GetUtcNow() + cache.Ttl, candidates.NextChange);
            var frozen = memo.ToFrozenDecisions(transientOmissions);
            var hasTransient = transientOmissions is { Count: > 0 };
            if (frozen.Count > 0 || !hasTransient)
            {
                await cache.TrySetAsync(cacheKey, caller.Tenant, new AccessViewEntry(candidates, frozen, stamp, notAfter, hasTransient), ct).ConfigureAwait(false);
            }

            return memo;
        }
        finally
        {
            gate.Release();
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>
    /// Issue #1044: the view cache key of the caller (tenant, user, channel and a hash over every claim except per-token bookkeeping the
    /// policy never reads), or null when the view must not be cached: no cache, a stale view found by the page guard, or degraded group
    /// resolution. The cache itself refuses while its invalidation signal is degraded or authorization hooks are active.
    /// </summary>
    private string? AccessViewCacheKey(CatalogCaller caller, SecurityPrincipalContext subject)
    {
        if (_accessViewCache is null ||
            _viewCacheBypassed ||
            subject.GroupResolutionStatus != Autheris.Domain.Identity.GroupResolutionStatus.Complete ||
            Autheris.Domain.Identity.GroupResolutionClaims.IsMarkedDegraded(caller.Principal!))
        {
            return null;
        }

        // Length-prefixed fields: no claim value can imitate a claim boundary.
        var claims = caller.Principal!.Claims
            .Where(static c => !ViewKeyIgnoredClaims.Contains(c.Type))
            .Select(static c => string.Create(CultureInfo.InvariantCulture, $"{c.Type.Length}:{c.Type}{c.Issuer.Length}:{c.Issuer}{c.Value.Length}:{c.Value}"))
            .Order(StringComparer.Ordinal);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(claims))));
        return string.Join('|', caller.Tenant.Value, subject.UserSid.Value, caller.Channel.ToString(), hash);
    }

    /// <summary>Per-token or per-request bookkeeping claims that no access decision reads; every other claim is part of the view key.</summary>
    private static readonly FrozenSet<string> ViewKeyIgnoredClaims = new[]
    {
        "exp", "iat", "nbf", "jti", "uti", "rh", "aio", "auth_time", "autheris:group_resolution_expires"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Issue #1044 (page guard): true when a listed item's fresh access state contradicts the cached view that put it on the page. The
    /// view entry is dropped and the caller lists again without the cache.
    /// </summary>
    private bool ContradictsCachedView(ScopeMemoKey scopeKey, DiscoveryAccessState wanted, IEnumerable<DiscoveryObject> items)
    {
        CallerAccessMemo? memo;
        lock (_memoLock)
        {
            _accessMemo.TryGetValue(scopeKey, out memo);
        }

        if (memo?.CacheKey is not { } cacheKey || items.All(i => i.Access == wanted))
        {
            return false;
        }

        _accessViewCache!.Remove(cacheKey);
        _viewCacheBypassed = true;
        lock (_memoLock)
        {
            _accessMemo.Clear();
            _objectMemo.Clear();
        }

        return true;
    }

    /// <summary>Issue #798: the procedures whose resolved caller state is <paramref name="wanted"/> (procedures are few; they resolve as on a page).</summary>
    private async Task<List<EligibleDiscoveryProcedure>> FilterProceduresByAccessAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        List<EligibleDiscoveryProcedure> procedures,
        DiscoveryAccessState wanted,
        CancellationToken ct)
    {
        if (procedures.Count == 0)
        {
            return procedures;
        }

        var resolved = await ResolveProceduresBatchAsync(caller, sid, scopeKey, procedures, ct).ConfigureAwait(false);
        if (resolved.Count != procedures.Count)
        {
            throw new InvalidOperationException("Procedure resolution returned a mismatched count.");
        }

        var kept = new List<EligibleDiscoveryProcedure>(procedures.Count);
        for (var i = 0; i < procedures.Count; i++)
        {
            if (resolved[i].Access == wanted)
            {
                kept.Add(procedures[i]);
            }
        }

        return kept;
    }

    private async Task<IReadOnlyList<DiscoveryObject>> ResolveObjectsBatchAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        IReadOnlyList<TableMetadata> tables,
        string? language,
        CancellationToken ct)
    {
        if (tables.Count == 0)
        {
            return [];
        }

        var langKey = string.IsNullOrWhiteSpace(language) ? string.Empty : language.Trim().ToLowerInvariant();
        var results = new DiscoveryObject?[tables.Count];
        var uncached = new List<(int Index, TableMetadata Table)>(tables.Count);

        lock (_memoLock)
        {
            for (var i = 0; i < tables.Count; i++)
            {
                var objKey = new ObjectMemoKey(scopeKey, DiscoveryObjectKind.Table, tables[i].Identifier, langKey);
                if (_objectMemo.TryGetValue(objKey, out var cached))
                {
                    results[i] = cached;
                }
                else
                {
                    uncached.Add((i, tables[i]));
                }
            }
        }

        if (uncached.Count == 0)
        {
            return results.Where(static r => r is not null).Select(static r => r!).ToList();
        }

        var uncachedTables = uncached.Select(static x => x.Table).ToList();
        var uncachedIds = uncachedTables.Select(static t => t.Identifier).ToList();

        var ownersTask = _ownershipRepository is null
            ? Task.FromResult<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>>(new Dictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>())
            : _ownershipRepository.GetDataOwnersForTablesAsync(uncachedIds, ct);

        var subject = SecurityPrincipalContext.FromPrincipal(caller.Principal!, caller.Tenant);

        // Issue #1047: the read and the write capabilities of the page (canonical PDP path, zero data reads) share one batch read.
        var decisionsTask = _decisionPoint!.DecideOperationsManyAsync(
            subject, uncachedTables, [AccessOperation.Discover, AccessOperation.Insert, AccessOperation.Update, AccessOperation.Delete], AccessTransport.Discovery, ct).AsTask();

        var pendingTask = LoadPendingRequestTablesAsync(sid, caller.Tenant, ct);
        var expiredTask = LoadRecentlyExpiredTablesAsync(sid, caller.Tenant, ct);

        await Task.WhenAll(ownersTask, decisionsTask, pendingTask, expiredTask).ConfigureAwait(false);

        var ownersByTable = await ownersTask.ConfigureAwait(false);
        var byOperation = await decisionsTask.ConfigureAwait(false);
        var pendingTables = await pendingTask.ConfigureAwait(false);
        var expiredTables = await expiredTask.ConfigureAwait(false);

        if (byOperation.Count != 4 || byOperation.Any(d => d.Count != uncachedTables.Count))
        {
            throw new InvalidOperationException("AccessDecisionPoint returned mismatched decision count.");
        }

        var (decisions, insertDecisions, updateDecisions, deleteDecisions) = (byOperation[0], byOperation[1], byOperation[2], byOperation[3]);
        var prefetchNow = _timeProvider.GetUtcNow();
        var pdp = _decisionPoint as AccessDecisionPoint;
        var colPrefetch = pdp?.LastColumnRowPrefetch;
        var defs = _classificationRepository is null
            ? ClassificationAccessResolver.BuiltInCatalog(caller.Tenant.Value).Definitions
            : colPrefetch?.CatalogFor(caller.Tenant.Value, prefetchNow)?.Definitions
              ?? ClassificationHierarchyValidator.ResolveEffective(
                  await _classificationRepository.GetClassificationsAsync(caller.Tenant.Value, includeInactive: true, ct).ConfigureAwait(false), caller.Tenant.Value);
        var docLinks = await LoadDocumentLinksAsync(caller.Tenant.Value, uncachedTables, ct).ConfigureAwait(false);
        var unclassifiedColumns = _classificationRepository is null
            ? []
            : uncachedTables.Where(static t => !t.Table.DiscoveryHidden)
                .SelectMany(static t => t.Columns.Where(c => !c.DiscoveryHidden && !t.ColumnClassifications.ContainsKey(c.ColumnName)))
                .Select(static c => c.Id.ToString()).Distinct().ToList();
        var classificationsByColumnId = await ResolveUnclassifiedColumnsAsync(caller.Tenant.Value, unclassifiedColumns, colPrefetch, prefetchNow, ct).ConfigureAwait(false);
        TenantAccessPolicy? tenantPolicy = _accessPolicySettings is null ? null : await _accessPolicySettings.GetEffectiveAsync(caller.Tenant, ct).ConfigureAwait(false);
        var batchProtections = await ResolveBatchProtectionsWithPrefetchAsync(caller.Tenant, uncachedTables, pdp?.LastProtectionPrefetch, prefetchNow, ct).ConfigureAwait(false);

        var artifactsByTableId = _artifactStore is null
            ? null
            : await _artifactStore.GetAllForTablesAsync(uncachedTables.Select(static t => t.Table.Id).ToList(), ct).ConfigureAwait(false);

        for (var i = 0; i < uncached.Count; i++)
        {
            var (targetIdx, table) = uncached[i];
            var decision = decisions[i];
            var owners = ownersByTable.TryGetValue(table.Identifier, out var list) ? list : Array.Empty<ContainerOwnerEntry>();
            var isPending = !decision.IsAllowed && pendingTables.Contains(table.Identifier);
            var isExpired = !decision.IsAllowed && !isPending && expiredTables.Contains(table.Identifier);
            IReadOnlyList<DiscoveryArtifacts> allArtifacts = artifactsByTableId is not null &&
                artifactsByTableId.TryGetValue(table.Table.Id, out var loadedArtifacts)
                ? loadedArtifacts
                : [];
            var artifacts = allArtifacts.FirstOrDefault(a => a.Provenance == ArtifactProvenance.Upload)
                ?? allArtifacts.FirstOrDefault(a => a.Provenance != ArtifactProvenance.Import);
            var importArtifact = allArtifacts.FirstOrDefault(a => a.Provenance == ArtifactProvenance.Import && !string.IsNullOrWhiteSpace(a.SamplesJson));
            var importSamplesAvailable = importArtifact is not null
                && (tenantPolicy?.ServeImportSamples ?? true)
                && !(tenantPolicy?.QuarantineSuspectedPiiSamples == true && CatalogPiiDetector.JsonHasSuspectedPii(importArtifact.SamplesJson));
            Dictionary<string, IReadOnlyList<ColumnClassification>>? repoColClasses = null;
            if (_classificationRepository is not null && !table.Table.DiscoveryHidden)
            {
                foreach (var col in table.Columns.Where(c => !c.DiscoveryHidden && !table.ColumnClassifications.ContainsKey(c.ColumnName)))
                {
                    if (classificationsByColumnId.TryGetValue(col.Id.ToString(), out var rows) && rows.Count > 0)
                    {
                        (repoColClasses ??= new(StringComparer.OrdinalIgnoreCase))[col.ColumnName] = rows;
                    }
                }
            }

            var protection = _protectionProvider is null
                ? null
                : batchProtections is not null && batchProtections.TryGetValue(table.Identifier, out var batchProtection)
                    ? batchProtection
                    : await _protectionProvider.GetAsync(caller.Tenant, table.Identifier, ct).ConfigureAwait(false);
            var mapped = DiscoveryAccessMapper.Map(
                table, decision, owners, isPending, isExpired, artifacts, language,
                isHidden: table.Table.DiscoveryHidden, insertDecisions[i], updateDecisions[i], deleteDecisions[i], defs, repoColClasses, caller.Tenant.Value,
                hideMaskedTextForRestrictedScopes: _discoveryOptions.Discovery?.HideMaskedColumnCatalogTextForRestrictedScopes == true,
                effectiveProtection: protection,
                importSamplesAvailable: importSamplesAvailable);

            if (docLinks.TryGetValue(table.Identifier, out var linkView))
            {
                mapped = linkView.ApplyTo(mapped);
            }

            results[targetIdx] = mapped;
            lock (_memoLock)
            {
                _objectMemo[new ObjectMemoKey(scopeKey, DiscoveryObjectKind.Table, table.Identifier, langKey)] = mapped;
                if (protection is not null) _protectionMemo[(caller.Tenant, table.Identifier)] = protection;
            }
        }

        return results.Where(static r => r is not null).Select(static r => r!).ToList();
    }

    /// <summary>The caller's grant sources and the decided tables of one scope (issue #798); mutated only under <c>_memoLock</c>.</summary>
    private sealed record CallerAccessMemo(ReadGrantCandidates Candidates)
    {
        /// <summary>The decisions made in this request (they take precedence over the cached base).</summary>
        public Dictionary<TableIdentifier, bool> Allowed { get; } = [];
        public IReadOnlySet<TableIdentifier>? Pending { get; set; }
        public IReadOnlySet<TableIdentifier>? Expired { get; set; }

        /// <summary>Issue #1044: the view cache key when the decisions came from (or were stored in) the cross-request cache.</summary>
        public string? CacheKey { get; private init; }

        // Issue #1044: the cached decisions, shared read-only (never copied per request), minus the stale lower-case table keys.
        private IReadOnlyDictionary<TableIdentifier, bool>? _cached;
        private IReadOnlySet<string>? _stale;

        public bool IsCandidate(TableMetadata table) => Candidates.IsCandidate(table);

        public bool TryGetAllowed(TableIdentifier table, out bool allowed) =>
            Allowed.TryGetValue(table, out allowed) ||
            (_cached is not null && _cached.TryGetValue(table, out allowed) && (_stale is not { Count: > 0 } || !_stale.Contains(table.ToString().ToLowerInvariant())));

        /// <summary>The cached decisions that are still valid, overlaid with non-transient decisions of this request.</summary>
        public FrozenDictionary<TableIdentifier, bool> ToFrozenDecisions(IReadOnlySet<TableIdentifier>? transientOmissions = null)
        {
            var merged = new Dictionary<TableIdentifier, bool>();
            foreach (var (table, isAllowed) in Allowed)
            {
                if (transientOmissions is null || !transientOmissions.Contains(table))
                {
                    merged[table] = isAllowed;
                }
            }

            foreach (var (table, isAllowed) in _cached ?? FrozenDictionary<TableIdentifier, bool>.Empty)
            {
                if (!merged.ContainsKey(table) &&
                    (transientOmissions is null || !transientOmissions.Contains(table)) &&
                    TryGetAllowed(table, out _))
                {
                    merged[table] = isAllowed;
                }
            }

            return merged.ToFrozenDictionary();
        }

        /// <summary>A memo on top of cached decisions, except those of the <paramref name="stale"/> lower-case table keys.</summary>
        public static CallerAccessMemo FromView(
            ReadGrantCandidates candidates, IReadOnlyDictionary<TableIdentifier, bool> allowed, IReadOnlySet<string>? stale, string cacheKey) =>
            new(candidates) { CacheKey = cacheKey, _cached = allowed, _stale = stale };
    }

    private sealed record CallerAccessView(
        IReadOnlySet<TableIdentifier> Granted,
        IReadOnlySet<TableIdentifier> Pending,
        IReadOnlySet<TableIdentifier> Expired)
    {
        public DiscoveryAccessState StateOf(TableIdentifier table) =>
            DiscoveryAccessMapper.AccessStateOf(Granted.Contains(table), Pending.Contains(table), Expired.Contains(table));
    }

    private async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>>> ResolveUnclassifiedColumnsAsync(
        string tenantId, List<string> columnIds, ColumnPolicyRowPrefetch? colPrefetch, DateTimeOffset now, CancellationToken ct)
    {
        if (columnIds.Count == 0) return new Dictionary<string, IReadOnlyList<ColumnClassification>>();
        if (colPrefetch is not null && colPrefetch.Covers(tenantId, columnIds, now))
        {
            var dict = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in columnIds) { var rows = colPrefetch.ClassificationsOf(id); if (rows.Count > 0) dict[id] = rows; }
            return dict;
        }
        return await _classificationRepository!.GetColumnClassificationsBatchAsync(columnIds, tenantId, ct).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyDictionary<TableIdentifier, EffectiveProtection>?> ResolveBatchProtectionsWithPrefetchAsync(
        TenantId tenant, IReadOnlyList<TableMetadata> tables, ProtectionBatchPrefetch? protPrefetch, DateTimeOffset now, CancellationToken ct)
    {
        if (_protectionProvider is null) return null;
        if (protPrefetch is null) return await _protectionProvider.ResolveManyAsync(tenant, tables, ct).ConfigureAwait(false);
        var map = new Dictionary<TableIdentifier, EffectiveProtection>(tables.Count);
        List<TableMetadata>? missing = null;
        foreach (var t in tables) { if (protPrefetch.TryGet(tenant, t.Identifier, now, out var p)) map[t.Identifier] = p; else (missing ??= []).Add(t); }
        if (missing is { Count: > 0 })
            foreach (var kv in await _protectionProvider.ResolveManyAsync(tenant, missing, ct).ConfigureAwait(false)) map[kv.Key] = kv.Value;
        return map;
    }
}
