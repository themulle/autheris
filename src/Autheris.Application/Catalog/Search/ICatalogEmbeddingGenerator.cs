namespace Autheris.Application.Catalog.Search;

using Autheris.Domain.Interfaces;

/// <summary>
/// Honest embedding generator interface for catalog search, supporting availability checks
/// and graceful degradation.
/// </summary>
public interface ICatalogEmbeddingGenerator : ILocalEmbeddingGenerator
{
    /// <summary>
    /// Indicates whether the embedding generator is currently available and healthy.
    /// </summary>
    bool IsAvailable { get; }
}
