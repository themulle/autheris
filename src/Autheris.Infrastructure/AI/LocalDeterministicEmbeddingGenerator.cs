namespace Autheris.Infrastructure.AI;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Interfaces;

/// <summary>
/// Vollständig lokaler, deterministischer In-Process Embedding Generator.
/// Projiziert Schema-Begriffe und Intentionen in einen 384-dimensionalen Einheitsvektor-Raum,
/// ohne externe Modelle, Python oder Cloud-Abhängigkeiten.
/// </summary>
public sealed class LocalDeterministicEmbeddingGenerator : ILocalEmbeddingGenerator
{
    private const int Dimensions = 384;

    public int EmbeddingDimensions => Dimensions;
    public string ModelIdentifier => "Deterministic-Local-Projection-384";

    public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new float[Dimensions];

        var tokens = SmartSchemaTokenizer.Tokenize(text, expandAbbreviations: true);
        if (tokens.Length == 0) return new float[Dimensions];

        var vector = new float[Dimensions];

        for (int t = 0; t < tokens.Length; t++)
        {
            var token = tokens[t];
            ulong hash = ComputeHash(token);

            // Projiziere Token auf Dimensionen
            for (int d = 0; d < 8; d++)
            {
                int index = (int)((hash + (ulong)(d * 47)) % (ulong)Dimensions);
                float sign = ((hash >> (d * 3)) & 1) == 0 ? 1.0f : -1.0f;
                vector[index] += sign * (1.0f / (1.0f + (t * 0.1f)));
            }
        }

        NormalizeInPlace(vector);
        return vector;
    }

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(
        IReadOnlyList<string> texts, 
        CancellationToken ct = default)
    {
        var results = new ReadOnlyMemory<float>[texts.Count];

        Parallel.For(0, texts.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            results[i] = GenerateNormalizedEmbedding(texts[i]);
        });

        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(results);
    }

    private static void NormalizeInPlace(Span<float> vector)
    {
        float sumSquares = 0f;
        for (int i = 0; i < vector.Length; i++)
        {
            sumSquares += vector[i] * vector[i];
        }

        if (sumSquares <= 0f) return;
        float invNorm = 1.0f / MathF.Sqrt(sumSquares);

        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] *= invNorm;
        }
    }

    private static ulong ComputeHash(string str)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < str.Length; i++)
        {
            hash ^= (byte)str[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
