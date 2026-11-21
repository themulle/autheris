namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed partial class DiscoveryService
{
    /// <summary>
    /// Issue #978: shared catalog search matching and optional relevance ranking across REST list, keyset, facets, and MCP list_datasets.
    /// Uses <see cref="_searchEngine"/> (BM25 / hybrid / semantic) when the index is ready, and term-based matching otherwise,
    /// always followed by per-caller visible-text verification so hidden or denied columns never match.
    /// </summary>
    private async Task<List<TableMetadata>> FilterAndRankBySearchAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        List<TableMetadata> candidates,
        string searchText,
        string? domainFilter,
        string? mode,
        string? language,
        bool orderByRelevance,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        if (_searchEngine?.IsIndexReady == true && _searchEngine.IndexedTableCount > 0)
        {
            var candidateById = candidates.ToDictionary(static t => t.Identifier);
            var hits = await _searchEngine.SearchAsync(
                new CatalogSearchQuery(
                    QueryText: searchText,
                    DomainFilter: domainFilter,
                    Limit: candidates.Count,
                    Mode: ParseSearchMode(mode),
                    Language: language),
                id => candidateById.ContainsKey(id),
                isColumnRestricted: null,
                ct).ConfigureAwait(false);

            var indexCandidates = new List<TableMetadata>(hits.Count);
            foreach (var hit in hits)
            {
                if (candidateById.TryGetValue(hit.TableIdentifier, out var meta))
                {
                    indexCandidates.Add(meta);
                }
            }
            foreach (var table in candidates)
            {
                if (!_searchEngine.ContainsTable(table.Identifier) &&
                    DiscoveryVisibleTextMatcher.MatchesNonHiddenTextTerms(table, searchText))
                {
                    indexCandidates.Add(table);
                }
            }

            var restrictedScores = new Dictionary<TableIdentifier, int>();
            var verified = await KeepCallerVisibleMatchesAsync(
                caller,
                sid,
                scopeKey,
                indexCandidates,
                language,
                (table, projected) => DiscoveryVisibleTextMatcher.MatchesVisibleSearch(table, projected, searchText),
                ct,
                restrictedScores,
                (table, projected) => DiscoveryVisibleTextMatcher.VisibleTermMatchCount(table, projected, searchText)).ConfigureAwait(false);

            if (orderByRelevance && restrictedScores.Count > 0)
            {
                var unrestricted = verified.Where(t => !restrictedScores.ContainsKey(t.Identifier));
                var restrictedOrdered = verified
                    .Where(t => restrictedScores.ContainsKey(t.Identifier))
                    .OrderByDescending(t => restrictedScores[t.Identifier])
                    .ThenBy(t => t.Identifier.ToString(), StringComparer.OrdinalIgnoreCase);
                verified = unrestricted.Concat(restrictedOrdered).ToList();
            }

            return verified;
        }

        var termCandidates = new List<TableMetadata>(candidates.Count);
        foreach (var table in candidates)
        {
            if (DiscoveryVisibleTextMatcher.MatchesNonHiddenTextTerms(table, searchText))
            {
                termCandidates.Add(table);
            }
        }

        var fallbackRestricted = orderByRelevance ? new Dictionary<TableIdentifier, int>() : null;
        var verifiedFallback = await KeepCallerVisibleMatchesAsync(
            caller,
            sid,
            scopeKey,
            termCandidates,
            language,
            (table, projected) => DiscoveryVisibleTextMatcher.MatchesVisibleSearch(table, projected, searchText),
            ct,
            fallbackRestricted,
            orderByRelevance ? (table, projected) => DiscoveryVisibleTextMatcher.VisibleTermMatchCount(table, projected, searchText) : null).ConfigureAwait(false);

        if (orderByRelevance && verifiedFallback.Count > 1)
        {
            verifiedFallback = verifiedFallback
                .OrderByDescending(t => fallbackRestricted!.TryGetValue(t.Identifier, out var s)
                    ? s
                    : DiscoveryVisibleTextMatcher.NonHiddenTermMatchCount(t, searchText))
                .ThenBy(t => t.Identifier.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.ToString(), StringComparer.Ordinal)
                .ToList();
        }

        return verifiedFallback;
    }
}
