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

        if (options.EmbeddingProvider == CatalogEmbeddingProvider.LocalOnnx)
        {
            if (string.IsNullOrWhiteSpace(options.ModelPath))
            {
                return ValidateOptionsResult.Fail("ModelPath must be configured when EmbeddingProvider is LocalOnnx.");
            }

            if (options.ModelPath.Contains("..", StringComparison.Ordinal))
            {
                return ValidateOptionsResult.Fail("ModelPath must not contain path traversal segments ('..').");
            }

            if (!File.Exists(options.ModelPath))
            {
                return ValidateOptionsResult.Fail($"ONNX model file not found at ModelPath '{options.ModelPath}'.");
            }

            if (!string.IsNullOrWhiteSpace(options.TokenizerPath))
            {
                if (options.TokenizerPath.Contains("..", StringComparison.Ordinal))
                {
                    return ValidateOptionsResult.Fail("TokenizerPath must not contain path traversal segments ('..').");
                }

                if (!File.Exists(options.TokenizerPath))
                {
                    return ValidateOptionsResult.Fail($"ONNX tokenizer file not found at TokenizerPath '{options.TokenizerPath}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(options.ModelSha256) &&
                !LocalOnnxEmbeddingGenerator.ValidateModelFileIntegrity(options.ModelPath, options.ModelSha256))
            {
                return ValidateOptionsResult.Fail("ONNX model SHA-256 integrity validation failed for ModelPath.");
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

            if (OllamaEmbeddingGenerator.IsLoopbackHost(uri.Host))
            {
                return ValidateOptionsResult.Fail(
                    "OllamaEndpoint must not be a loopback address: the outbound egress policy refuses it. Use a host name or address that is listed in Gateway:Egress:TrustedInternalHosts or TrustedInternalNetworks, with CatalogEmbeddings in Gateway:Egress:TrustedIntegrations.");
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
