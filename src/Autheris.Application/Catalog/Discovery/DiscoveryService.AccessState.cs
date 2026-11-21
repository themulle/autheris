namespace Autheris.Application.Catalog.Discovery;

using System;
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
        var memo = await LoadAccessMemoAsync(caller, subject, scopeKey, ct).ConfigureAwait(false);

        List<TableMetadata>? undecided = null;
        lock (_memoLock)
        {
            foreach (var table in tables)
            {
                if (!memo.Allowed.ContainsKey(table.Identifier) && memo.IsCandidate(table))
                {
                    (undecided ??= []).Add(table);
                }
            }
        }

        if (undecided is not null)
        {
            var decisions = await _decisionPoint!.DecideManyAsync(
                subject, undecided, AccessOperation.Discover, AccessTransport.Discovery, ct).ConfigureAwait(false);
            if (decisions.Count != undecided.Count)
            {
                throw new InvalidOperationException("AccessDecisionPoint returned mismatched decision count.");
            }

            lock (_memoLock)
            {
                for (var i = 0; i < undecided.Count; i++)
                {
                    memo.Allowed[undecided[i].Identifier] = decisions[i].IsAllowed;
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
                if (memo.Allowed.TryGetValue(table.Identifier, out var allowed) && allowed)
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

        ReadGrantCandidates candidates;
        if (_gatewayOptions.IsConsentBypassed)
        {
            candidates = ReadGrantCandidates.All;
        }
        else
        {
            var subjects = caller.Principal!.GetAllUserSids();
            subjects.Add(subject.UserSid);
            subjects.UnionWith(subject.GroupSids);
            candidates = await ReadGrantCandidates.LoadAsync(_consentRepository!, subjects, _timeProvider.GetUtcNow(), caller.Tenant, ct).ConfigureAwait(false);
        }

        var memo = new CallerAccessMemo(candidates);

        lock (_memoLock)
        {
            return _accessMemo.TryAdd(scopeKey, memo) ? memo : _accessMemo[scopeKey];
        }
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
        public Dictionary<TableIdentifier, bool> Allowed { get; } = [];
        public IReadOnlySet<TableIdentifier>? Pending { get; set; }
        public IReadOnlySet<TableIdentifier>? Expired { get; set; }

        public bool IsCandidate(TableMetadata table) => Candidates.IsCandidate(table);
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
