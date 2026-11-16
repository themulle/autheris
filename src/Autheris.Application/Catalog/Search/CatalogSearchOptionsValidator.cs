namespace Autheris.Application.Catalog.Search;

using System;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class CatalogSearchOptionsValidator : IValidateOptions<CatalogSearchOptions>
{
    public ValidateOptionsResult Validate(string? name, CatalogSearchOptions options)
    {
        if (options == null)
        {
            return ValidateOptionsResult.Fail("CatalogSearchOptions cannot be null.");
        }

        if (options.RefreshSeconds < 0 || (options.RefreshSeconds > 0 && options.RefreshSeconds < 5))
        {
            return ValidateOptionsResult.Fail("RefreshSeconds must be 0 (disabled) or at least 5 seconds.");
        }

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (options.EmbeddingProvider == CatalogEmbeddingProvider.LocalOnnx)
        {
            return ValidateOptionsResult.Fail("LocalOnnx embedding provider is not available in this build.");
        }
        else if (options.EmbeddingProvider == CatalogEmbeddingProvider.Ollama)
        {
            if (string.IsNullOrWhiteSpace(options.OllamaEndpoint))
            {
                return ValidateOptionsResult.Fail("OllamaEndpoint must be configured when EmbeddingProvider is Ollama.");
            }

            if (!Uri.TryCreate(options.OllamaEndpoint, UriKind.Absolute, out var uri))
            {
                return ValidateOptionsResult.Fail($"OllamaEndpoint '{options.OllamaEndpoint}' is not a valid absolute URI.");
            }

            if (uri.Scheme != Uri.UriSchemeHttps && !options.OllamaInsecureAllowHttp)
            {
                return ValidateOptionsResult.Fail("OllamaEndpoint requires HTTPS unless OllamaInsecureAllowHttp is true.");
            }

            if (string.IsNullOrWhiteSpace(options.OllamaModelName))
            {
                return ValidateOptionsResult.Fail("OllamaModelName must be configured when EmbeddingProvider is Ollama.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
