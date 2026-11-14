namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using Autheris.Domain.Common;

/// <summary>
/// Hardware-accelerated immutable in-memory vector index supporting incremental delta segments and tombstones.
/// Evaluates cosine similarity over pre-normalized unit vectors via SIMD DotProduct.
/// </summary>
public sealed class VectorSearchIndex
{
    private readonly (TableIdentifier TableId, float[] Vector)[] _baseRecords;
    private readonly IReadOnlyDictionary<TableIdentifier, int> _baseTableToIndex;
    private readonly IReadOnlySet<int> _baseTombstones;
    private readonly IReadOnlyDictionary<TableIdentifier, float[]> _deltaMap;
    private readonly (TableIdentifier TableId, float[] Vector)[] _deltaRecords;

    public VectorSearchIndex(IEnumerable<(TableIdentifier TableId, ReadOnlyMemory<float> Vector)> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);

        var records = new List<(TableIdentifier TableId, float[] Vector)>();
        var baseTableToIndex = new Dictionary<TableIdentifier, int>();

        foreach (var v in vectors)
        {
            if (v.Vector.IsEmpty)
            {
                if (baseTableToIndex.Remove(v.TableId, out var priorIdx))
                {
                    records[priorIdx] = (v.TableId, []);
                }
                continue;
            }

            if (baseTableToIndex.TryGetValue(v.TableId, out var existingIdx))
            {
                records[existingIdx] = (v.TableId, v.Vector.ToArray());
                continue;
            }

            baseTableToIndex[v.TableId] = records.Count;
            records.Add((v.TableId, v.Vector.ToArray()));
        }

        _baseRecords = records.Where(r => r.Vector.Length > 0).ToArray();
        _baseTableToIndex = BuildIndexMap(_baseRecords);
        _baseTombstones = new HashSet<int>();
        _deltaMap = new Dictionary<TableIdentifier, float[]>();
        _deltaRecords = [];
    }

    private VectorSearchIndex(
        (TableIdentifier TableId, float[] Vector)[] baseRecords,
        IReadOnlyDictionary<TableIdentifier, int> baseTableToIndex,
        IReadOnlySet<int> baseTombstones,
        IReadOnlyDictionary<TableIdentifier, float[]> deltaMap)
    {
        _baseRecords = baseRecords;
        _baseTableToIndex = baseTableToIndex;
        _baseTombstones = baseTombstones;
        _deltaMap = deltaMap;
        _deltaRecords = deltaMap.Count == 0
            ? []
            : deltaMap.Select(kvp => (kvp.Key, kvp.Value)).ToArray();
    }

    public int Count => _baseRecords.Length - _baseTombstones.Count + _deltaRecords.Length;

    public int BaseCount => _baseRecords.Length;

    public int TombstoneCount => _baseTombstones.Count;

    public int DeltaOperationCount => _baseTombstones.Count + _deltaRecords.Length;

    public bool TryGetVector(TableIdentifier tableId, out ReadOnlyMemory<float> vector)
    {
        if (_deltaMap.TryGetValue(tableId, out var deltaVec))
        {
            vector = deltaVec;
            return true;
        }

        if (_baseTableToIndex.TryGetValue(tableId, out int baseIdx) && !_baseTombstones.Contains(baseIdx))
        {
            vector = _baseRecords[baseIdx].Vector;
            return true;
        }

        vector = ReadOnlyMemory<float>.Empty;
        return false;
    }

    public VectorSearchIndex ApplyDelta(
        IEnumerable<(TableIdentifier TableId, ReadOnlyMemory<float> Vector)> upserts,
        IEnumerable<TableIdentifier> removals)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removals);

        var removalSet = removals as HashSet<TableIdentifier> ?? [.. removals];
        var tombstones = new HashSet<int>(_baseTombstones);
        var deltaMap = new Dictionary<TableIdentifier, float[]>(_deltaMap);

        void TombstoneIfInBase(TableIdentifier id)
        {
            if (_baseTableToIndex.TryGetValue(id, out int idx))
            {
                tombstones.Add(idx);
            }
        }

        foreach (var (tableId, vector) in upserts)
        {
            if (removalSet.Contains(tableId))
            {
                continue;
            }

            TombstoneIfInBase(tableId);
            if (vector.IsEmpty)
            {
                deltaMap.Remove(tableId);
                continue;
            }

            deltaMap[tableId] = vector.ToArray();
        }

        foreach (var removedId in removalSet)
        {
            deltaMap.Remove(removedId);
            TombstoneIfInBase(removedId);
        }

        return new VectorSearchIndex(_baseRecords, _baseTableToIndex, tombstones, deltaMap);
    }

    public IReadOnlyList<(TableIdentifier TableId, double Score)> Search(
        ReadOnlyMemory<float> queryNormalizedVector,
        float minScore = 0.20f,
        int topK = 25,
        Func<TableIdentifier, bool>? isAllowed = null)
    {
        if (Count == 0 || queryNormalizedVector.IsEmpty) return [];
        var querySpan = queryNormalizedVector.Span;

        var results = new List<(TableIdentifier TableId, double Score)>();

        for (int i = 0; i < _baseRecords.Length; i++)
        {
            // Skip tombstoned entries before predicate evaluation or similarity computation.
            if (_baseTombstones.Contains(i))
            {
                continue;
            }

            ref readonly var item = ref _baseRecords[i];
            if (isAllowed != null && !isAllowed(item.TableId))
            {
                continue;
            }

            EvaluateItem(querySpan, item.TableId, item.Vector, minScore, results);
        }

        for (int i = 0; i < _deltaRecords.Length; i++)
        {
            ref readonly var item = ref _deltaRecords[i];
            if (isAllowed != null && !isAllowed(item.TableId))
            {
                continue;
            }

            EvaluateItem(querySpan, item.TableId, item.Vector, minScore, results);
        }

        return results.OrderByDescending(x => x.Score).Take(topK).ToList();
    }

    private static void EvaluateItem(
        ReadOnlySpan<float> querySpan,
        TableIdentifier tableId,
        float[] vector,
        float minScore,
        List<(TableIdentifier TableId, double Score)> results)
    {
        if (vector.Length != querySpan.Length)
        {
            return;
        }

        float similarity = TensorPrimitives.CosineSimilarity(querySpan, vector);
        if (similarity >= minScore)
        {
            results.Add((tableId, similarity));
        }
    }

    private static Dictionary<TableIdentifier, int> BuildIndexMap((TableIdentifier TableId, float[] Vector)[] records)
    {
        var map = new Dictionary<TableIdentifier, int>(records.Length);
        for (int i = 0; i < records.Length; i++)
        {
            map[records[i].TableId] = i;
        }
        return map;
    }
}
