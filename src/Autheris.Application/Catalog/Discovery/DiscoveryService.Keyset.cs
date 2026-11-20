namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed partial class DiscoveryService
{
    public async ValueTask<DiscoveryKeysetPage> ListKeysetAsync(
        DiscoveryKeysetQuery query,
        CatalogCaller caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(caller);
        ct.ThrowIfCancellationRequested();

        var sid = await DiscoveryCallerGate.EnsureAllowedAsync(
            caller, _discoveryOptions, _gatewayOptions, _rateLimiter, ct).ConfigureAwait(false);

        if (_metadataRepository is null || _decisionPoint is null || _consentRepository is null)
        {
            RecordFailure("Required discovery dependency missing.");
            return new DiscoveryKeysetPage([], query.CountTotal ? 0 : null, null);
        }

        try
        {
            var scopeKey = BuildScopeMemoKey(sid, caller);
            var searchText = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
            var orderByRelevance = query.OrderByRelevance && searchText is not null;

            var candidates = await LoadPreFilteredTablesAsync(
                caller,
                sid,
                scopeKey,
                query.Domain,
                query.Schema,
                query.Kind,
                query.Tag,
                query.Sensitivity,
                searchText,
                query.Mode,
                query.Language,
                orderByRelevance,
                query.Access,
                query.MetadataPredicate,
                ct).ConfigureAwait(false);

            var first = Math.Clamp(query.First <= 0 ? 50 : query.First, 1, MaxPageSize);
            int startIndex;
            if (orderByRelevance)
            {
                startIndex = 0;
                if (!string.IsNullOrEmpty(query.AfterKey))
                {
                    var foundIdx = candidates.FindIndex(t => string.Equals(t.Identifier.ToString(), query.AfterKey, StringComparison.OrdinalIgnoreCase));
                    startIndex = foundIdx >= 0 ? foundIdx + 1 : candidates.Count;
                }
            }
            else
            {
                candidates.Sort(static (a, b) => CompareTableKeys(a.Identifier.ToString(), b.Identifier.ToString()));
                startIndex = string.IsNullOrEmpty(query.AfterKey)
                    ? 0
                    : FindFirstIndexAfter(candidates, query.AfterKey);
            }

            var hasPostFilter = query.ProjectedPredicate is not null;

            List<DiscoveryKeysetItem> pageItems;
            int? totalCount;
            bool hasMore;

            if (!hasPostFilter)
            {
                totalCount = query.CountTotal ? candidates.Count : null;
                var takeCount = Math.Min(first, Math.Max(0, candidates.Count - startIndex));
                hasMore = startIndex + takeCount < candidates.Count;
                pageItems = new List<DiscoveryKeysetItem>(takeCount);
                if (takeCount > 0)
                {
                    var slice = candidates.GetRange(startIndex, takeCount);
                    var projected = await ResolveObjectsBatchAsync(
                        caller, sid, scopeKey, slice, query.Language, ct).ConfigureAwait(false);
                    for (var i = 0; i < projected.Count; i++)
                    {
                        pageItems.Add(new DiscoveryKeysetItem(projected[i], slice[i]));
                    }
                }
            }
            else if (query.CountTotal)
            {
                (pageItems, hasMore, var exactCount) = await FillAndCountWithPostFilterAsync(
                    caller, sid, scopeKey, candidates, startIndex, first, query.Language, searchText, query.ProjectedPredicate, ct).ConfigureAwait(false);
                totalCount = exactCount;
            }
            else
            {
                (pageItems, hasMore) = await FillWindowWithPostFilterAsync(
                    caller, sid, scopeKey, candidates, startIndex, first, query.Language, searchText, query.ProjectedPredicate, ct).ConfigureAwait(false);
                totalCount = null;
            }

            var nextKey = hasMore && pageItems.Count > 0
                ? pageItems[^1].Table.Identifier.ToString()
                : null;

            await WriteKeysetDiscoveryAuditAsync(
                caller.Tenant, sid, query, pageItems, searchText, ct).ConfigureAwait(false);

            return new DiscoveryKeysetPage(pageItems, totalCount, nextKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DiscoveryStatusException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(ex, "Discovery ListKeysetAsync failed closed.");
            return new DiscoveryKeysetPage([], query.CountTotal ? 0 : null, null);
        }
    }

    internal static int CompareTableKeys(string a, string b)
    {
        var cmp = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a, b);
    }

    internal static int FindFirstIndexAfter(List<TableMetadata> sorted, string afterKey)
    {
        var lo = 0;
        var hi = sorted.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (CompareTableKeys(sorted[mid].Identifier.ToString(), afterKey) <= 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private async Task<List<TableMetadata>> LoadPreFilteredTablesAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        string? domain,
        string? schema,
        DiscoveryObjectKind? kind,
        string? tag,
        string? sensitivity,
        string? search,
        string? mode,
        string? language,
        bool orderByRelevance,
        DiscoveryAccessState? access,
        Func<TableMetadata, bool>? metadataPredicate,
        CancellationToken ct)
    {
        var trimmedDomain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        var trimmedSchema = string.IsNullOrWhiteSpace(schema) ? null : schema.Trim();
        var scoped = await LoadScopedCatalogAsync(caller, sid, scopeKey, trimmedDomain, trimmedSchema, ct).ConfigureAwait(false);
        var trimmedTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        var trimmedSensitivity = string.IsNullOrWhiteSpace(sensitivity) ? null : sensitivity.Trim();
        var trimmedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        var matched = new List<TableMetadata>(scoped.VisibleTables.Count);
        foreach (var table in scoped.VisibleTables)
        {
            if (trimmedDomain is not null && !string.Equals(table.Identifier.Domain, trimmedDomain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmedSchema is not null && !string.Equals(table.Identifier.Schema, trimmedSchema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (kind is { } k && table.Table.ObjectKind != k)
            {
                continue;
            }

            if (trimmedTag is not null && !HasEffectiveTag(table, trimmedTag))
            {
                continue;
            }

            if (trimmedSensitivity is not null && !string.Equals(table.Table.Sensitivity, trimmedSensitivity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (metadataPredicate is not null && !metadataPredicate(table))
            {
                continue;
            }

            matched.Add(table);
        }

        if (access is { } wantedAccess)
        {
            var view = await LoadAccessViewAsync(caller, sid, scopeKey, matched, wantedAccess, ct).ConfigureAwait(false);
            matched = matched.FindAll(t => view.StateOf(t.Identifier) == wantedAccess);
        }

        if (trimmedSearch is not null)
        {
            matched = await FilterAndRankBySearchAsync(
                caller, sid, scopeKey, matched, trimmedSearch, trimmedDomain, mode, language, orderByRelevance, ct).ConfigureAwait(false);
        }

        return matched;
    }

    private async Task<(List<DiscoveryKeysetItem> PageItems, bool HasMore, int TotalCount)> FillAndCountWithPostFilterAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        List<TableMetadata> candidates,
        int startIndex,
        int first,
        string? language,
        string? searchText,
        Func<DiscoveryObject, bool>? projectedPredicate,
        CancellationToken ct)
    {
        var pageItems = new List<DiscoveryKeysetItem>(first);
        var hasMore = false;
        var totalCount = 0;

        for (var offset = 0; offset < candidates.Count; offset += MaxPageSize)
        {
            var chunkLen = Math.Min(MaxPageSize, candidates.Count - offset);
            var batch = candidates.GetRange(offset, chunkLen);
            var projected = await ResolveObjectsBatchAsync(caller, sid, scopeKey, batch, language, ct).ConfigureAwait(false);
            if (projected.Count != batch.Count)
            {
                throw new InvalidOperationException("Discovery projection returned a mismatched object count.");
            }

            for (var i = 0; i < batch.Count; i++)
            {
                if (!MatchesPostProjection(batch[i], projected[i], searchText, projectedPredicate))
                {
                    continue;
                }

                totalCount++;
                if (offset + i < startIndex)
                {
                    continue;
                }

                if (pageItems.Count < first)
                {
                    pageItems.Add(new DiscoveryKeysetItem(projected[i], batch[i]));
                }
                else
                {
                    hasMore = true;
                }
            }
        }

        return (pageItems, hasMore, totalCount);
    }

    private async Task<(List<DiscoveryKeysetItem> PageItems, bool HasMore)> FillWindowWithPostFilterAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        List<TableMetadata> candidates,
        int startIndex,
        int first,
        string? language,
        string? searchText,
        Func<DiscoveryObject, bool>? projectedPredicate,
        CancellationToken ct)
    {
        var windowSize = Math.Min(first + 1, MaxPageSize);
        var collected = new List<DiscoveryKeysetItem>(first + 1);

        for (var offset = startIndex; offset < candidates.Count && collected.Count < first + 1; offset += windowSize)
        {
            var chunkLen = Math.Min(windowSize, candidates.Count - offset);
            var window = candidates.GetRange(offset, chunkLen);
            var projected = await ResolveObjectsBatchAsync(caller, sid, scopeKey, window, language, ct).ConfigureAwait(false);
            if (projected.Count != window.Count)
            {
                throw new InvalidOperationException("Discovery projection returned a mismatched object count.");
            }

            for (var i = 0; i < window.Count; i++)
            {
                if (!MatchesPostProjection(window[i], projected[i], searchText, projectedPredicate))
                {
                    continue;
                }

                collected.Add(new DiscoveryKeysetItem(projected[i], window[i]));
                if (collected.Count >= first + 1)
                {
                    break;
                }
            }
        }

        var hasMore = collected.Count > first;
        var pageItems = hasMore ? collected.GetRange(0, first) : collected;
        return (pageItems, hasMore);
    }

    private static bool MatchesPostProjection(
        TableMetadata table,
        DiscoveryObject projected,
        string? searchText,
        Func<DiscoveryObject, bool>? projectedPredicate)
    {
        if (searchText is not null && !DiscoveryVisibleTextMatcher.MatchesVisibleSearch(table, projected, searchText))
        {
            return false;
        }

        return projectedPredicate is null || projectedPredicate(projected);
    }

    private async Task WriteKeysetDiscoveryAuditAsync(
        TenantId tenant,
        Sid actorSid,
        DiscoveryKeysetQuery query,
        IReadOnlyList<DiscoveryKeysetItem> items,
        string? searchText,
        CancellationToken ct)
    {
        if (_auditWriter is null)
        {
            return;
        }

        var objectIds = items
            .Select(static item => $"{item.Object.Domain}.{item.Object.Schema}.{item.Object.Name}")
            .ToList();

        var appliedFilters = CollectAppliedFilterNames(query, searchText);
        var queryHash = searchText is null ? null : ComputeQueryHash(searchText);

        var entry = new AuditLogEntry
        {
            TenantId = tenant,
            OccurredAt = _timeProvider.GetUtcNow(),
            ActorSid = actorSid,
            EventType = AuditEventTypes.CatalogRead,
            TargetTable = objectIds.Count == 1 ? objectIds[0] : "*",
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new
            {
                surface = SurfaceName(query.Surface),
                objectCount = items.Count,
                objectIds,
                appliedFilters,
                queryHash,
                tenant = tenant.Value
            })
        };

        await _auditWriter.RecordAsync(entry, ct).ConfigureAwait(false);
    }

    private static List<string> CollectAppliedFilterNames(DiscoveryKeysetQuery query, string? searchText)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Domain)) names.Add("domain");
        if (!string.IsNullOrWhiteSpace(query.Schema)) names.Add("schema");
        if (query.Kind.HasValue) names.Add("kind");
        if (!string.IsNullOrWhiteSpace(query.Tag)) names.Add("tag");
        if (!string.IsNullOrWhiteSpace(query.Sensitivity)) names.Add("sensitivity");
        if (query.Access.HasValue) names.Add("accessStatus");
        if (searchText is not null) names.Add("q");
        if (!string.IsNullOrEmpty(query.AfterKey)) names.Add("cursor");

        if (query.AppliedFilterNames is { Count: > 0 })
        {
            foreach (var extra in query.AppliedFilterNames)
            {
                if (!string.IsNullOrWhiteSpace(extra) && !names.Contains(extra, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(extra);
                }
            }
        }

        return names;
    }
}
