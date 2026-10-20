namespace Autheris.Application.Catalog.Search;

using System;
using System.IO;
using System.Text.RegularExpressions;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed partial class CatalogSearchOptionsValidator : IValidateOptions<CatalogSearchOptions>
{
    [GeneratedRegex("^[a-fA-F0-9]{64}$")]
    private static partial Regex Sha256Regex();

    public ValidateOptionsResult Validate(string? name, CatalogSearchOptions options)
    {
        if (options == null)
        {
            return ValidateOptionsResult.Fail("CatalogSearchOptions cannot be null.");
        }

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (options.EmbeddingProvider == CatalogEmbeddingProvider.LocalOnnx)
        {
            if (string.IsNullOrWhiteSpace(options.ModelPath))
            {
                return ValidateOptionsResult.Fail("ModelPath must be configured when EmbeddingProvider is LocalOnnx.");
            }

            if (string.IsNullOrWhiteSpace(options.TokenizerPath))
            {
                return ValidateOptionsResult.Fail("TokenizerPath must be configured when EmbeddingProvider is LocalOnnx.");
            }

            if (options.ModelPath.Contains("..", StringComparison.Ordinal) || options.TokenizerPath.Contains("..", StringComparison.Ordinal))
            {
                return ValidateOptionsResult.Fail("Path traversal detected in ModelPath or TokenizerPath.");
            }

            if (!File.Exists(options.ModelPath))
            {
                return ValidateOptionsResult.Fail($"ModelPath file not found: {options.ModelPath}");
            }

            if (!File.Exists(options.TokenizerPath))
            {
                return ValidateOptionsResult.Fail($"TokenizerPath file not found: {options.TokenizerPath}");
            }

            if (string.IsNullOrWhiteSpace(options.ModelSha256) || !Sha256Regex().IsMatch(options.ModelSha256))
            {
                return ValidateOptionsResult.Fail("ModelSha256 must be a valid 64-character hex string when EmbeddingProvider is LocalOnnx.");
            }
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
