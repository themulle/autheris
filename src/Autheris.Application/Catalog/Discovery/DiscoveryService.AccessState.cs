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

        // A current entry is used without waiting for anyone; only misses and revalidations are serialized per key.
        if (await cache.ReadStampAsync(ct).ConfigureAwait(false) is { } early && cache.TryGet(cacheKey, early) is { Stale.Count: 0 } current)
        {
            return CallerAccessMemo.FromView(current.Entry.Candidates, current.Entry.Allowed, stale: null, cacheKey);
        }

        var gate = cache.GateFor(cacheKey);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await cache.ReadStampAsync(ct).ConfigureAwait(false) is not { } stamp)
            {
                return new CallerAccessMemo(await LoadGrantCandidatesAsync(caller, subject, ct).ConfigureAwait(false));
            }

            var hit = cache.TryGet(cacheKey, stamp);
            if (hit is { Stale.Count: 0 } fresh)
            {
                return CallerAccessMemo.FromView(fresh.Entry.Candidates, fresh.Entry.Allowed, stale: null, cacheKey);
            }

            var candidates = await LoadGrantCandidatesAsync(caller, subject, ct).ConfigureAwait(false);
            var memo = hit is { } revalidate
                ? CallerAccessMemo.FromView(candidates, revalidate.Entry.Allowed, revalidate.Stale, cacheKey)
                : CallerAccessMemo.FromView(candidates, FrozenDictionary<TableIdentifier, bool>.Empty, stale: null, cacheKey);

            var scoped = await LoadScopedCatalogAsync(caller, sid, scopeKey, ct).ConfigureAwait(false);
            var undecided = scoped.VisibleTables.Where(t => memo.IsCandidate(t) && !memo.TryGetAllowed(t.Identifier, out _)).ToList();
            if (undecided.Count > 0)
            {
                var allowed = await _decisionPoint!.DecideAllowedManyAsync(subject, undecided, ct).ConfigureAwait(false);
                if (allowed.Count != undecided.Count)
                {
                    throw new InvalidOperationException("AccessDecisionPoint returned mismatched decision count.");
                }

                for (var i = 0; i < undecided.Count; i++)
                {
                    memo.Allowed[undecided[i].Identifier] = allowed[i];
                }
            }

            var notAfter = Min(hit?.Entry.NotAfter ?? cache.Time.GetUtcNow() + cache.Ttl, candidates.NextChange);
            await cache.TrySetAsync(cacheKey, new AccessViewEntry(candidates, memo.ToFrozenDecisions(), stamp, notAfter), ct).ConfigureAwait(false);
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

        /// <summary>The cached decisions that are still valid, overlaid with the decisions of this request.</summary>
        public FrozenDictionary<TableIdentifier, bool> ToFrozenDecisions()
        {
            var merged = new Dictionary<TableIdentifier, bool>(Allowed);
            foreach (var (table, isAllowed) in _cached ?? FrozenDictionary<TableIdentifier, bool>.Empty)
            {
                if (!merged.ContainsKey(table) && TryGetAllowed(table, out _))
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
}
