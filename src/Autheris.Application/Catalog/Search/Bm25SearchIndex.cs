namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

/// <summary>The closed set of fields the index scores separately (BM25F). Not user-extensible.</summary>
public enum Bm25Field
{
    TechnicalName = 0,
    DisplayName = 1,
    TagsAndOwner = 2,
    ShortDescription = 3,
    LongDescription = 4,
    ColumnNames = 5,
    ColumnDescriptions = 6,
}

/// <summary>Internal field weights of the catalog search ranking (values of the removed <c>FieldWeights</c> option).</summary>
public static class CatalogSearchFieldWeights
{
    /// <summary>Number of scored fields; the memory per document is bounded by this constant.</summary>
    public const int FieldCount = 7;

    /// <summary>Default weights in <see cref="Bm25Field"/> order.</summary>
    public static IReadOnlyList<double> Default { get; } = [10.0, 8.0, 5.0, 3.0, 2.0, 2.0, 1.0];
}

/// <summary>One table as per-field texts for <see cref="Bm25SearchIndex"/>.</summary>
public sealed record Bm25Document(
    TableIdentifier TableId,
    string TechnicalName = "",
    string DisplayName = "",
    string TagsAndOwner = "",
    string ShortDescription = "",
    string LongDescription = "",
    string ColumnNames = "",
    string ColumnDescriptions = "")
{
    internal string TextOf(int field) => (Bm25Field)field switch
    {
        Bm25Field.TechnicalName => TechnicalName,
        Bm25Field.DisplayName => DisplayName,
        Bm25Field.TagsAndOwner => TagsAndOwner,
        Bm25Field.ShortDescription => ShortDescription,
        Bm25Field.LongDescription => LongDescription,
        Bm25Field.ColumnNames => ColumnNames,
        Bm25Field.ColumnDescriptions => ColumnDescriptions,
        _ => string.Empty,
    };
}

/// <summary>
/// Thread-safe immutable Okapi BM25F index with weighted per-field scoring, incremental delta segments, and tombstones.
/// Uses the Robertson-Sparck Jones IDF formula with smoothing.
/// </summary>
public sealed class Bm25SearchIndex
{
    private const double K1 = 1.5;
    private const double B = 0.75;
    private const int FirstColumnField = (int)Bm25Field.ColumnNames;

    /// <summary>Query tokens shorter than this match whole words only.</summary>
    public const int MinPartialMatchLength = 4;

    /// <summary>Indexed tokens one query token may expand to (bounds the cost of a query).</summary>
    public const int MaxPartialExpansions = 32;

    /// <summary>Query tokens scored per search; further tokens are ignored (availability bound).</summary>
    public const int MaxQueryTerms = 64;

    /// <summary>A partial match scores this fraction of a whole-word match of the same field statistics.</summary>
    private const double PartialMatchFactor = 0.4;

    private sealed record IndexedDocument(
        TableIdentifier TableId,
        Dictionary<string, int>?[] FieldTermFrequencies,
        int[] FieldLengths,
        bool HasTokens);

    private readonly IReadOnlyList<IndexedDocument> _baseDocuments;
    private readonly IReadOnlyDictionary<TableIdentifier, int> _baseTableToIndex;
    private readonly IReadOnlyDictionary<string, int> _baseTermDocCounts;
    private readonly IReadOnlyDictionary<string, int> _baseTableLevelTermCounts;
    private readonly IReadOnlySet<int> _baseTombstones;
    private readonly IReadOnlyDictionary<TableIdentifier, IndexedDocument> _deltaDocuments;
    private readonly IReadOnlyDictionary<string, int> _termDocCountDeltas;
    private readonly IReadOnlyDictionary<string, int> _tableLevelTermCountDeltas;
    private readonly int _activeDocCount;
    private readonly long[] _totalFieldLengths;
    private readonly double[] _weights;

    // Distinct indexed tokens ordered by (length, ordinal): partial matches prefer the tokens closest to the whole word.
    // The table-level vocabulary excludes tokens that occur only in column fields, so a caller with hidden columns
    // never gets an expansion (or a cap slot) taken by hidden column text.
    private readonly string[] _vocabulary;
    private readonly string[] _tableLevelVocabulary;

    /// <summary>Single-field convenience: the whole text is indexed as the short description.</summary>
    public Bm25SearchIndex(IEnumerable<(TableIdentifier TableId, string Content)> source)
        : this(source.Select(static x => new Bm25Document(x.TableId, ShortDescription: x.Content)))
    {
    }

    public Bm25SearchIndex(IEnumerable<Bm25Document> source, IReadOnlyList<double>? fieldWeights = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _weights = ValidateWeights(fieldWeights ?? CatalogSearchFieldWeights.Default);

        var documents = new List<IndexedDocument>();
        var baseTableToIndex = new Dictionary<TableIdentifier, int>();
        var termDocCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tableLevelTermCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalFieldLengths = new long[CatalogSearchFieldWeights.FieldCount];

        foreach (var entry in source)
        {
            if (baseTableToIndex.TryGetValue(entry.TableId, out var priorIdx))
            {
                // Replace duplicate identifier cleanly so base segment never holds stale duplicate postings.
                var prior = documents[priorIdx];
                AdjustStats(prior, termDocCounts, tableLevelTermCounts, totalFieldLengths, delta: -1);

                var replacement = TokenizeDocument(entry);
                if (replacement is null)
                {
                    baseTableToIndex.Remove(entry.TableId);
                    documents[priorIdx] = new IndexedDocument(
                        entry.TableId,
                        new Dictionary<string, int>?[CatalogSearchFieldWeights.FieldCount],
                        new int[CatalogSearchFieldWeights.FieldCount],
                        HasTokens: false);
                    continue;
                }

                documents[priorIdx] = replacement;
                AdjustStats(replacement, termDocCounts, tableLevelTermCounts, totalFieldLengths, delta: +1);
                continue;
            }

            var doc = TokenizeDocument(entry);
            if (doc is null) continue;

            baseTableToIndex[entry.TableId] = documents.Count;
            documents.Add(doc);
            AdjustStats(doc, termDocCounts, tableLevelTermCounts, totalFieldLengths, delta: +1);
        }

        _baseDocuments = documents;
        _baseTableToIndex = baseTableToIndex;
        _baseTermDocCounts = termDocCounts;
        _baseTableLevelTermCounts = tableLevelTermCounts;
        _baseTombstones = new HashSet<int>();
        _deltaDocuments = new Dictionary<TableIdentifier, IndexedDocument>();
        _termDocCountDeltas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _tableLevelTermCountDeltas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _activeDocCount = baseTableToIndex.Count;
        _totalFieldLengths = totalFieldLengths;
        _vocabulary = BuildVocabulary(termDocCounts);
        _tableLevelVocabulary = BuildVocabulary(tableLevelTermCounts);
    }

    private Bm25SearchIndex(
        IReadOnlyList<IndexedDocument> baseDocuments,
        IReadOnlyDictionary<TableIdentifier, int> baseTableToIndex,
        IReadOnlyDictionary<string, int> baseTermDocCounts,
        IReadOnlyDictionary<string, int> baseTableLevelTermCounts,
        IReadOnlySet<int> baseTombstones,
        IReadOnlyDictionary<TableIdentifier, IndexedDocument> deltaDocuments,
        IReadOnlyDictionary<string, int> termDocCountDeltas,
        IReadOnlyDictionary<string, int> tableLevelTermCountDeltas,
        int activeDocCount,
        long[] totalFieldLengths,
        double[] weights)
    {
        _baseDocuments = baseDocuments;
        _baseTableToIndex = baseTableToIndex;
        _baseTermDocCounts = baseTermDocCounts;
        _baseTableLevelTermCounts = baseTableLevelTermCounts;
        _baseTombstones = baseTombstones;
        _deltaDocuments = deltaDocuments;
        _termDocCountDeltas = termDocCountDeltas;
        _tableLevelTermCountDeltas = tableLevelTermCountDeltas;
        _activeDocCount = activeDocCount;
        _totalFieldLengths = totalFieldLengths;
        _weights = weights;
        _vocabulary = BuildVocabulary(baseTermDocCounts, termDocCountDeltas);
        _tableLevelVocabulary = BuildVocabulary(baseTableLevelTermCounts, tableLevelTermCountDeltas);
    }

    public int ActiveDocumentCount => _activeDocCount;

    public int BaseDocumentCount => _baseTableToIndex.Count;

    public int TombstoneCount => _baseTombstones.Count;

    public int DeltaOperationCount => _baseTombstones.Count + _deltaDocuments.Count;

    public Bm25SearchIndex ApplyDelta(
        IEnumerable<(TableIdentifier TableId, string Content)> upserts,
        IEnumerable<TableIdentifier> removals)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        return ApplyDelta(
            upserts.Select(static x => new Bm25Document(x.TableId, ShortDescription: x.Content)),
            removals);
    }

    public Bm25SearchIndex ApplyDelta(
        IEnumerable<Bm25Document> upserts,
        IEnumerable<TableIdentifier> removals)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removals);

        var removalSet = removals as HashSet<TableIdentifier> ?? [.. removals];
        var tombstones = new HashSet<int>(_baseTombstones);
        var deltaDocuments = new Dictionary<TableIdentifier, IndexedDocument>(_deltaDocuments);
        var termDeltas = new Dictionary<string, int>(_termDocCountDeltas, StringComparer.OrdinalIgnoreCase);
        var tableLevelTermDeltas = new Dictionary<string, int>(_tableLevelTermCountDeltas, StringComparer.OrdinalIgnoreCase);
        int activeDocCount = _activeDocCount;
        var totalFieldLengths = (long[])_totalFieldLengths.Clone();

        void RemoveActive(TableIdentifier id)
        {
            if (deltaDocuments.Remove(id, out var existingDelta))
            {
                activeDocCount--;
                AdjustStats(existingDelta, termDeltas, tableLevelTermDeltas, totalFieldLengths, delta: -1);
                return;
            }

            if (_baseTableToIndex.TryGetValue(id, out int baseIdx) && tombstones.Add(baseIdx))
            {
                activeDocCount--;
                AdjustStats(_baseDocuments[baseIdx], termDeltas, tableLevelTermDeltas, totalFieldLengths, delta: -1);
            }
        }

        foreach (var entry in upserts)
        {
            if (removalSet.Contains(entry.TableId))
            {
                continue;
            }

            RemoveActive(entry.TableId);

            var doc = TokenizeDocument(entry);
            if (doc is null)
            {
                continue;
            }

            deltaDocuments[entry.TableId] = doc;
            activeDocCount++;
            AdjustStats(doc, termDeltas, tableLevelTermDeltas, totalFieldLengths, delta: +1);
        }

        foreach (var removedId in removalSet)
        {
            RemoveActive(removedId);
        }

        return new Bm25SearchIndex(
            _baseDocuments,
            _baseTableToIndex,
            _baseTermDocCounts,
            _baseTableLevelTermCounts,
            tombstones,
            deltaDocuments,
            termDeltas,
            tableLevelTermDeltas,
            activeDocCount,
            totalFieldLengths,
            _weights);
    }

    private static string[] BuildVocabulary(
        IReadOnlyDictionary<string, int> baseCounts,
        IReadOnlyDictionary<string, int>? deltaCounts = null)
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, count) in baseCounts)
        {
            int total = count + (deltaCounts?.GetValueOrDefault(term) ?? 0);
            if (total > 0) active.Add(term);
        }

        if (deltaCounts is not null)
        {
            foreach (var (term, delta) in deltaCounts)
            {
                if (!baseCounts.ContainsKey(term) && delta > 0) active.Add(term);
            }
        }

        return SortVocabulary(active);
    }

    private static string[] SortVocabulary(IEnumerable<string> tokens)
    {
        var array = tokens.ToArray();
        Array.Sort(array, static (a, b) =>
        {
            int byLength = a.Length.CompareTo(b.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
        });
        return array;
    }

    /// <summary>
    /// Indexed tokens that contain <paramref name="term"/> (ordinal; tokens are already lower-cased invariantly by the tokenizer),
    /// shortest first, at most <see cref="MaxPartialExpansions"/>. Empty for tokens below <see cref="MinPartialMatchLength"/>.
    /// </summary>
    private static List<string> Expand(string term, string[] vocabulary)
    {
        var result = new List<string>();
        if (term.Length < MinPartialMatchLength) return result;

        foreach (var candidate in vocabulary)
        {
            if (candidate.Length <= term.Length) continue;
            if (candidate.Contains(term, StringComparison.Ordinal))
            {
                result.Add(candidate);
                if (result.Count >= MaxPartialExpansions) break;
            }
        }

        return result;
    }

    private sealed record QueryTermPlan(string Term, double? Idf, List<(string Token, double Idf)> Expansions);

    /// <summary>Corpus statistics one query is scored against: document count, document frequency and expansion vocabulary.</summary>
    private sealed record CorpusStats(int DocCount, Func<string, int> DocumentFrequency, string[] Vocabulary);

    private static double? ComputeIdf(CorpusStats stats, string term)
    {
        int df = stats.DocumentFrequency(term);
        if (df <= 0) return null;

        // Robertson-Sparck Jones IDF with smoothing
        return Math.Log(1.0 + (stats.DocCount - df + 0.5) / (df + 0.5));
    }

    private static QueryTermPlan[] BuildPlan(string[] queryTerms, CorpusStats stats, string[] vocabulary)
    {
        int count = Math.Min(queryTerms.Length, MaxQueryTerms);
        var plan = new QueryTermPlan[count];
        for (int i = 0; i < count; i++)
        {
            var term = queryTerms[i];
            double? idf = ComputeIdf(stats, term);
            var expansions = new List<(string Token, double Idf)>();
            foreach (var candidate in Expand(term, vocabulary))
            {
                if (ComputeIdf(stats, candidate) is { } expansionIdf)
                {
                    expansions.Add((candidate, expansionIdf));
                }
            }

            plan[i] = new QueryTermPlan(term, idf, expansions);
        }

        return plan;
    }

    private List<IndexedDocument> CollectVisibleDocuments(Func<TableIdentifier, bool>? isAllowed)
    {
        var visible = new List<IndexedDocument>(_activeDocCount);
        for (int i = 0; i < _baseDocuments.Count; i++)
        {
            var doc = _baseDocuments[i];
            if (_baseTombstones.Contains(i) || !doc.HasTokens || (isAllowed != null && !isAllowed(doc.TableId))) continue;
            visible.Add(doc);
        }

        foreach (var doc in _deltaDocuments.Values)
        {
            if (!doc.HasTokens || (isAllowed != null && !isAllowed(doc.TableId))) continue;
            visible.Add(doc);
        }

        return visible;
    }

    /// <summary>
    /// Statistics over the visible documents only: document count, field lengths, document frequency and expansion
    /// vocabulary. Restricted documents therefore cannot move a visible document's score, its rank or its partial matches.
    /// </summary>
    private static (CorpusStats Full, CorpusStats TableLevel, double[] AvgFieldLength) BuildVisibleStats(
        List<IndexedDocument> visible,
        string[] queryTerms)
    {
        var terms = queryTerms.Length > MaxQueryTerms ? queryTerms[..MaxQueryTerms] : queryTerms;
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var vocabAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vocabTableLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totals = new long[CatalogSearchFieldWeights.FieldCount];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in visible)
        {
            seen.Clear();
            for (int f = 0; f < CatalogSearchFieldWeights.FieldCount; f++)
            {
                totals[f] += doc.FieldLengths[f];
                var tf = doc.FieldTermFrequencies[f];
                if (tf is null) continue;

                foreach (var token in tf.Keys)
                {
                    if (!IsRelevantToken(token, terms)) continue;
                    if (seen.Add(token)) df[token] = df.GetValueOrDefault(token) + 1;
                    vocabAll.Add(token);
                    if (f < FirstColumnField) vocabTableLevel.Add(token);
                }
            }
        }

        var avg = new double[CatalogSearchFieldWeights.FieldCount];
        for (int f = 0; f < avg.Length; f++)
        {
            avg[f] = visible.Count == 0 ? 0.0 : (double)totals[f] / visible.Count;
        }

        Func<string, int> dfOf = t => df.GetValueOrDefault(t);
        return (
            new CorpusStats(visible.Count, dfOf, SortVocabulary(vocabAll)),
            new CorpusStats(visible.Count, dfOf, SortVocabulary(vocabTableLevel)),
            avg);
    }

    private static bool IsRelevantToken(string token, string[] queryTerms)
    {
        foreach (var term in queryTerms)
        {
            if (string.Equals(token, term, StringComparison.OrdinalIgnoreCase)) return true;
            if (term.Length >= MinPartialMatchLength && token.Length > term.Length && token.Contains(term, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static double PseudoTf(
        IndexedDocument doc,
        string token,
        double[] avgFieldLength,
        double[] weights,
        int fieldLimit)
    {
        double pseudoTf = 0.0;
        for (int f = 0; f < fieldLimit; f++)
        {
            var tfMap = doc.FieldTermFrequencies[f];
            if (tfMap is null || avgFieldLength[f] <= 0.0) continue;
            if (!tfMap.TryGetValue(token, out var tf)) continue;

            double norm = 1.0 - B + B * (doc.FieldLengths[f] / avgFieldLength[f]);
            pseudoTf += weights[f] * tf / norm;
        }

        return double.IsFinite(pseudoTf) ? pseudoTf : 0.0;
    }

    private static double Saturate(double idf, double pseudoTf) => idf * (pseudoTf * (K1 + 1.0) / (pseudoTf + K1));

    private static double[] ValidateWeights(IReadOnlyList<double> weights)
    {
        if (weights.Count != CatalogSearchFieldWeights.FieldCount)
        {
            throw new ArgumentException($"Exactly {CatalogSearchFieldWeights.FieldCount} field weights are required.", nameof(weights));
        }

        var copy = new double[weights.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            if (!double.IsFinite(weights[i]) || weights[i] <= 0.0)
            {
                throw new ArgumentException("Field weights must be positive finite numbers.", nameof(weights));
            }

            copy[i] = weights[i];
        }

        return copy;
    }

    /// <param name="isAllowed">Table visibility filter.</param>
    /// <param name="isColumnRestricted">Tables for which the caller cannot see every column: only the table-level fields
    /// (not column names or column descriptions) contribute to the score, so hidden column text cannot move the ranking.</param>
    public IReadOnlyList<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)> Search(
        string query,
        int topK = 25,
        Func<TableIdentifier, bool>? isAllowed = null,
        Func<TableIdentifier, bool>? isColumnRestricted = null)
    {
        var queryTerms = SmartSchemaTokenizer.Tokenize(query, expandAbbreviations: false);
        if (queryTerms.Length == 0 || _activeDocCount == 0) return [];

        // Authorization filtering happens before any corpus statistic is read: document count, field lengths, document
        // frequency and expansion vocabulary come from the visible documents only when anything is hidden.
        var visible = CollectVisibleDocuments(isAllowed);
        if (visible.Count == 0) return [];

        CorpusStats fullStats;
        CorpusStats tableLevelStats;
        var avgFieldLength = new double[CatalogSearchFieldWeights.FieldCount];
        if (visible.Count == _activeDocCount)
        {
            for (int f = 0; f < avgFieldLength.Length; f++)
            {
                avgFieldLength[f] = (double)_totalFieldLengths[f] / _activeDocCount;
            }

            int GlobalDf(string t) => _baseTermDocCounts.GetValueOrDefault(t) + _termDocCountDeltas.GetValueOrDefault(t);
            fullStats = new CorpusStats(_activeDocCount, GlobalDf, _vocabulary);
            tableLevelStats = new CorpusStats(_activeDocCount, GlobalDf, _tableLevelVocabulary);
        }
        else
        {
            (fullStats, tableLevelStats, avgFieldLength) = BuildVisibleStats(visible, queryTerms);
        }

        if (!Array.Exists(avgFieldLength, static a => a > 0.0)) return [];

        var fullPlan = BuildPlan(queryTerms, fullStats, fullStats.Vocabulary);
        if (!Array.Exists(fullPlan, static e => e.Idf.HasValue || e.Expansions.Count > 0))
        {
            return [];
        }

        var tableLevelPlan = isColumnRestricted != null ? BuildPlan(queryTerms, tableLevelStats, tableLevelStats.Vocabulary) : fullPlan;

        var results = new List<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)>();

        foreach (var doc in visible)
        {
            ScoreDocument(doc, fullPlan, tableLevelPlan, avgFieldLength, _weights, isAllowed: null, isColumnRestricted, results);
        }

        return results.OrderByDescending(x => x.Score).Take(topK).ToList();
    }

    private static void ScoreDocument(
        IndexedDocument doc,
        QueryTermPlan[] fullPlan,
        QueryTermPlan[] tableLevelPlan,
        double[] avgFieldLength,
        double[] weights,
        Func<TableIdentifier, bool>? isAllowed,
        Func<TableIdentifier, bool>? isColumnRestricted,
        List<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)> results)
    {
        if (!doc.HasTokens || (isAllowed != null && !isAllowed(doc.TableId)))
        {
            return;
        }

        bool restricted = isColumnRestricted != null && isColumnRestricted(doc.TableId);
        int fieldLimit = restricted ? FirstColumnField : CatalogSearchFieldWeights.FieldCount;
        var plan = restricted ? tableLevelPlan : fullPlan;

        double score = 0.0;
        List<string>? matched = null;

        foreach (var entry in plan)
        {
            // BM25F: weighted, per-field length-normalized pseudo term frequency
            double pseudoTf = entry.Idf is not null
                ? PseudoTf(doc, entry.Term, avgFieldLength, weights, fieldLimit)
                : 0.0;
            if (pseudoTf > 0.0)
            {
                score += Saturate(entry.Idf!.Value, pseudoTf);
                matched ??= [];
                matched.Add(entry.Term);
                continue;
            }

            // No whole-word match in this document: the best partial match counts, at a lower weight.
            double bestPartial = 0.0;
            string? bestToken = null;
            foreach (var (token, expansionIdf) in entry.Expansions)
            {
                double partialTf = PseudoTf(doc, token, avgFieldLength, weights, fieldLimit);
                if (partialTf <= 0.0) continue;

                double partial = PartialMatchFactor * Saturate(expansionIdf, partialTf);
                if (partial > bestPartial)
                {
                    bestPartial = partial;
                    bestToken = token;
                }
            }

            if (bestToken is not null)
            {
                score += bestPartial;
                matched ??= [];
                matched.Add(bestToken);
            }
        }

        if (double.IsFinite(score) && score > 0.001)
        {
            results.Add((doc.TableId, score, matched ?? (IReadOnlyList<string>)[]));
        }
    }

    private static IndexedDocument? TokenizeDocument(Bm25Document entry)
    {
        var tfs = new Dictionary<string, int>?[CatalogSearchFieldWeights.FieldCount];
        var lengths = new int[CatalogSearchFieldWeights.FieldCount];
        bool any = false;

        for (int f = 0; f < CatalogSearchFieldWeights.FieldCount; f++)
        {
            var text = entry.TextOf(f);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var tokens = SmartSchemaTokenizer.Tokenize(text, expandAbbreviations: true);
            if (tokens.Length == 0) continue;

            var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tokens)
            {
                tf[t] = tf.GetValueOrDefault(t) + 1;
            }

            tfs[f] = tf;
            lengths[f] = tokens.Length;
            any = true;
        }

        return any ? new IndexedDocument(entry.TableId, tfs, lengths, HasTokens: true) : null;
    }

    private static void AdjustStats(
        IndexedDocument doc,
        Dictionary<string, int> termCounts,
        Dictionary<string, int> tableLevelTermCounts,
        long[] totalFieldLengths,
        int delta)
    {
        if (!doc.HasTokens) return;

        var seenAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTableLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int f = 0; f < CatalogSearchFieldWeights.FieldCount; f++)
        {
            totalFieldLengths[f] += delta * (long)doc.FieldLengths[f];

            var tf = doc.FieldTermFrequencies[f];
            if (tf is null) continue;

            bool isTableLevel = f < FirstColumnField;
            foreach (var term in tf.Keys)
            {
                if (seenAll.Add(term))
                {
                    AddCountDelta(termCounts, term, delta);
                }

                if (isTableLevel && seenTableLevel.Add(term))
                {
                    AddCountDelta(tableLevelTermCounts, term, delta);
                }
            }
        }
    }

    private static void AddCountDelta(Dictionary<string, int> counts, string term, int delta)
    {
        int updated = counts.GetValueOrDefault(term) + delta;
        if (updated == 0)
        {
            counts.Remove(term);
        }
        else
        {
            counts[term] = updated;
        }
    }
}
