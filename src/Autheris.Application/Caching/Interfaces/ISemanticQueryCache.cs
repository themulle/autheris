namespace Autheris.Application.Caching.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IEmbeddingGenerator
{
    int Dimensions { get; }
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}

public interface ISemanticQueryCache
{
    Task<SemanticMatchResult> TryGetAsync(
        SemanticCacheKey key,
        float[] queryEmbedding,
        float similarityThreshold = 0.95f,
        long currentPolicyEpoch = 0,
        CancellationToken ct = default);

    Task SetAsync(
        SemanticCacheEntry entry,
        CancellationToken ct = default);

    Task InvalidateCollectionAsync(TableIdentifier collection, CancellationToken ct = default);
}
