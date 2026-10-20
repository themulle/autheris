namespace Autheris.Application.Catalog.Search;

/// <summary>
/// No-op embedding generator used when embedding provider is None or disabled.
/// Triggers graceful degradation to pure keyword BM25 search.
/// </summary>
public sealed class NoneEmbeddingGenerator : ICatalogEmbeddingGenerator
{
    public static readonly NoneEmbeddingGenerator Instance = new();

    public int EmbeddingDimensions => 0;
    public string ModelIdentifier => "none";
    public bool IsAvailable => false;

    public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text) =>
        ReadOnlyMemory<float>.Empty;

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        var result = new ReadOnlyMemory<float>[texts.Count];
        Array.Fill(result, ReadOnlyMemory<float>.Empty);
        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(result);
    }
}
