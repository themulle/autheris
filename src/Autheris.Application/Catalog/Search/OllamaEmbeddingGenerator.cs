using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Autheris.Application.Security;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Catalog.Search;

/// <summary>
/// Honest Ollama embedding generator with SSRF prevention and outgoing secret scrubbing.
/// </summary>
public sealed class OllamaEmbeddingGenerator : ICatalogEmbeddingGenerator
{
    private readonly HttpClient? _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly ILogger<OllamaEmbeddingGenerator>? _logger;
    private readonly bool _isValid;

    public int EmbeddingDimensions => _dimensions;
    public string ModelIdentifier => $"ollama:{_model}";
    public bool IsAvailable => _isValid;

    public OllamaEmbeddingGenerator(
        string? endpoint,
        string? model = "nomic-embed-text",
        int dimensions = 384,
        HttpClient? httpClient = null,
        ILogger<OllamaEmbeddingGenerator>? logger = null)
    {
        _endpoint = endpoint ?? string.Empty;
        _model = string.IsNullOrWhiteSpace(model) ? "nomic-embed-text" : model;
        _dimensions = dimensions;
        _httpClient = httpClient;
        _logger = logger;
        _isValid = ValidateEndpoint(_endpoint);
    }

    public static bool ValidateEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // SSRF Defense: reject cloud metadata endpoints (e.g. AWS/GCP/Azure 169.254.169.254)
        var host = uri.Host;
        if (string.Equals(host, "169.254.169.254", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".metadata", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text)
    {
        if (!_isValid || string.IsNullOrWhiteSpace(text))
        {
            return ReadOnlyMemory<float>.Empty;
        }

        // Synchronous generation via Async call with timeout or empty fallback
        try
        {
            var task = GenerateNormalizedEmbeddingsAsync(new[] { text }, CancellationToken.None);
            var results = task.GetAwaiter().GetResult();
            return results.Count > 0 ? results[0] : ReadOnlyMemory<float>.Empty;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Ollama embedding generation failed; degrading to BM25.");
            return ReadOnlyMemory<float>.Empty;
        }
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        if (!_isValid || texts.Count == 0 || _httpClient == null)
        {
            var empty = new ReadOnlyMemory<float>[texts.Count];
            Array.Fill(empty, ReadOnlyMemory<float>.Empty);
            return empty;
        }

        var results = new List<ReadOnlyMemory<float>>(texts.Count);
        foreach (var text in texts)
        {
            // Secret scrubbing on outgoing prompt
            var cleanText = SecretScrubber.Redact(text);
            try
            {
                var request = new OllamaEmbeddingRequest
                {
                    Model = _model,
                    Prompt = cleanText
                };

                using var response = await _httpClient.PostAsJsonAsync($"{_endpoint.TrimEnd('/')}/api/embeddings", request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: ct).ConfigureAwait(false);
                    if (data?.Embedding != null && data.Embedding.Length > 0)
                    {
                        var vec = data.Embedding;
                        NormalizeInPlace(vec);
                        results.Add(vec);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to call Ollama embedding endpoint; degrading to empty vector.");
            }

            results.Add(ReadOnlyMemory<float>.Empty);
        }

        return results;
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

    private sealed class OllamaEmbeddingRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;
    }

    private sealed class OllamaEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }
}
