using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Catalog.Search;

/// <summary>
/// Factory creating the configured <see cref="ICatalogEmbeddingGenerator"/> instance.
/// </summary>
public static class CatalogEmbeddingGeneratorFactory
{
    public static ICatalogEmbeddingGenerator Create(
        CatalogSearchOptions options,
        HttpClient? httpClient = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.EmbeddingProvider switch
        {
            CatalogEmbeddingProvider.None => NoneEmbeddingGenerator.Instance,
            CatalogEmbeddingProvider.LocalOnnx => new LocalOnnxEmbeddingGenerator(
                options.LocalOnnxModelPath,
                options.LocalOnnxModelSha256,
                options.EmbeddingDimensions,
                loggerFactory?.CreateLogger<LocalOnnxEmbeddingGenerator>()),
            CatalogEmbeddingProvider.Ollama => new OllamaEmbeddingGenerator(
                options.OllamaEndpoint,
                options.OllamaModel,
                options.EmbeddingDimensions,
                httpClient,
                loggerFactory?.CreateLogger<OllamaEmbeddingGenerator>()),
            _ => NoneEmbeddingGenerator.Instance
        };
    }
}
