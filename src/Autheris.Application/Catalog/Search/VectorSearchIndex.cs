namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using Autheris.Domain.Common;

/// <summary>
/// Hardware-beschleunigter In-Memory Vektor-Index.
/// Führt Ähnlichkeitsvergleiche über vorab L2-normierte Einheitsvektoren mittels SIMD DotProduct aus.
/// </summary>
public sealed class VectorSearchIndex
{
    private readonly (TableIdentifier TableId, float[] Vector)[] _records;

    public VectorSearchIndex(IEnumerable<(TableIdentifier TableId, ReadOnlyMemory<float> Vector)> vectors)
    {
        _records = vectors.Select(v => (v.TableId, v.Vector.ToArray())).ToArray();
    }

    public int Count => _records.Length;

    public IReadOnlyList<(TableIdentifier TableId, double Score)> Search(
        ReadOnlyMemory<float> queryNormalizedVector, 
        float minScore = 0.20f, 
        int topK = 25)
    {
        if (_records.Length == 0 || queryNormalizedVector.IsEmpty) return [];
        var querySpan = queryNormalizedVector.Span;

        var results = new List<(TableIdentifier TableId, double Score)>();

        for (int i = 0; i < _records.Length; i++)
        {
            ref readonly var item = ref _records[i];
            
            // SIMD AVX2 / AVX-512 Vektorberechnung (0 Allokationen!)
            float similarity = TensorPrimitives.CosineSimilarity(querySpan, item.Vector);

            if (similarity >= minScore)
            {
                results.Add((item.TableId, similarity));
            }
        }

        return results.OrderByDescending(x => x.Score).Take(topK).ToList();
    }
}
