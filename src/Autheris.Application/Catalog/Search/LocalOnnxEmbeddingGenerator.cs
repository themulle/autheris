using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Autheris.Application.Catalog.Search;

/// <summary>
/// In-process local ONNX embedding generator using <see cref="InferenceSession"/> and
/// <see cref="Microsoft.ML.Tokenizers"/> with constant-time SHA-256 integrity validation and path traversal defense.
/// </summary>
public sealed class LocalOnnxEmbeddingGenerator : ICatalogEmbeddingGenerator, IDisposable
{
    public const int MaxSequenceLength = 512;

    private readonly string? _modelPath;
    private readonly string? _expectedSha256;
    private readonly string? _tokenizerPath;
    private readonly ILogger<LocalOnnxEmbeddingGenerator>? _logger;
    private readonly bool _isValid;
    private readonly int _dimensions;
    private readonly Lock _inferenceLock = new();
    private InferenceSession? _session;
    private Tokenizer? _tokenizer;
    private string _inputIdsName = "input_ids";
    private string _attentionMaskName = "attention_mask";
    private string? _tokenTypeIdsName;
    private string _outputName = string.Empty;
    private int _clsTokenId = 101;
    private int _sepTokenId = 102;
    private bool _disposed;

    public int EmbeddingDimensions => _dimensions;
    public string ModelIdentifier => !string.IsNullOrWhiteSpace(_modelPath)
        ? $"local-onnx:{Path.GetFileNameWithoutExtension(_modelPath)}"
        : "local-onnx-multilingual";
    public bool IsAvailable => !_disposed && _isValid && _session is not null && _tokenizer is not null;
    internal bool IsValidConfiguration => _isValid;

    public LocalOnnxEmbeddingGenerator(
        string? modelPath,
        string? expectedSha256,
        int dimensions = 384,
        ILogger<LocalOnnxEmbeddingGenerator>? logger = null,
        string? tokenizerPath = null)
    {
        _modelPath = modelPath;
        _expectedSha256 = expectedSha256;
        _dimensions = dimensions;
        _logger = logger;
        _tokenizerPath = tokenizerPath;
        _isValid = ValidateModel();
        if (_isValid)
        {
            TryInitializeRuntime();
        }
    }

    public static bool ValidateModelFileIntegrity(string modelPath, string expectedSha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256Hex);
        if (modelPath.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Path traversal attempt detected in ONNX model path.", nameof(modelPath));
        if (!File.Exists(modelPath)) return false;

        byte[] expectedHashBytes;
        try { expectedHashBytes = Convert.FromHexString(expectedSha256Hex.Trim()); }
        catch (FormatException) { throw new ArgumentException("Expected SHA-256 is not a valid hex string.", nameof(expectedSha256Hex)); }

        using var stream = File.OpenRead(modelPath);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), expectedHashBytes);
    }

    private bool ValidateModel()
    {
        if (string.IsNullOrWhiteSpace(_modelPath))
        {
            _logger?.LogWarning("ONNX model path is not configured; local ONNX embedding is unavailable.");
            return false;
        }
        if (_modelPath.Contains("..", StringComparison.Ordinal))
        {
            _logger?.LogError("Security violation: path traversal detected in ONNX model path '{Path}'.", _modelPath);
            return false;
        }
        if (_tokenizerPath is not null && _tokenizerPath.Contains("..", StringComparison.Ordinal))
        {
            _logger?.LogError("Security violation: path traversal detected in ONNX tokenizer path '{Path}'.", _tokenizerPath);
            return false;
        }
        if (!File.Exists(_modelPath))
        {
            _logger?.LogWarning("ONNX model file not found at '{Path}'.", _modelPath);
            return false;
        }
        if (!string.IsNullOrWhiteSpace(_expectedSha256))
        {
            try
            {
                if (!ValidateModelFileIntegrity(_modelPath, _expectedSha256))
                {
                    _logger?.LogError("Security violation: ONNX model SHA-256 integrity check failed for '{Path}'.", _modelPath);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to validate ONNX model integrity for '{Path}'.", _modelPath);
                return false;
            }
        }
        return true;
    }

    private void TryInitializeRuntime()
    {
        var tokPath = ResolveTokenizerPath();
        if (tokPath is null)
        {
            _logger?.LogWarning("ONNX tokenizer file not configured or not found; local ONNX embedding is unavailable.");
            return;
        }
        try { (_tokenizer, _clsTokenId, _sepTokenId) = LoadTokenizer(tokPath); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to load ONNX tokenizer from '{Path}'.", tokPath); return; }
        if (_tokenizer is null) return;

        InferenceSession? session = null;
        try
        {
            using var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            session = new InferenceSession(_modelPath!, opts);
            var inputs = session.InputMetadata.Keys.ToList();
            _inputIdsName = inputs.FirstOrDefault(static n => string.Equals(n, "input_ids", StringComparison.OrdinalIgnoreCase))
                ?? inputs.FirstOrDefault() ?? "input_ids";
            _attentionMaskName = inputs.FirstOrDefault(static n => string.Equals(n, "attention_mask", StringComparison.OrdinalIgnoreCase))
                ?? "attention_mask";
            _tokenTypeIdsName = inputs.FirstOrDefault(static n => string.Equals(n, "token_type_ids", StringComparison.OrdinalIgnoreCase));
            _outputName = session.OutputMetadata.Keys.FirstOrDefault(static n =>
                    string.Equals(n, "sentence_embedding", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "last_hidden_state", StringComparison.OrdinalIgnoreCase))
                ?? session.OutputMetadata.Keys.FirstOrDefault() ?? string.Empty;

            if (session.OutputMetadata.TryGetValue(_outputName, out var outMeta) &&
                outMeta.Dimensions.Length > 0 && outMeta.Dimensions[^1] > 0 && outMeta.Dimensions[^1] != _dimensions)
            {
                _logger?.LogError("ONNX model output dimension {Actual} does not match configured EmbeddingDimensions {Expected}.",
                    outMeta.Dimensions[^1], _dimensions);
                session.Dispose();
                return;
            }
            _session = session;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            _logger?.LogWarning(ex, "Failed to initialize ONNX Runtime session for '{Path}'.", _modelPath);
        }
    }

    private string? ResolveTokenizerPath()
    {
        if (!string.IsNullOrWhiteSpace(_tokenizerPath)) return File.Exists(_tokenizerPath) ? _tokenizerPath : null;
        var dir = Path.GetDirectoryName(_modelPath);
        if (string.IsNullOrWhiteSpace(dir)) return null;
        foreach (var candidate in new[] { "tokenizer.json", "vocab.txt" })
        {
            var full = Path.Combine(dir, candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static (Tokenizer? Tokenizer, int ClsId, int SepId) LoadTokenizer(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return (BertTokenizer.Create(path), 101, 102);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("model", out var model) ||
            !model.TryGetProperty("vocab", out var vocabProp) ||
            vocabProp.ValueKind != JsonValueKind.Object)
            return (null, 101, 102);

        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, leaveOpen: true))
            foreach (var token in vocabProp.EnumerateObject().OrderBy(static p => p.Value.GetInt32()).Select(static p => p.Name))
                writer.WriteLine(token);
        ms.Position = 0;
        var clsId = vocabProp.TryGetProperty("[CLS]", out var clsEl) ? clsEl.GetInt32() : 101;
        var sepId = vocabProp.TryGetProperty("[SEP]", out var sepEl) ? sepEl.GetInt32() : 102;
        return (BertTokenizer.Create(ms), clsId, sepId);
    }

    public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return ReadOnlyMemory<float>.Empty;
        var batch = RunBatch([text]);
        return batch.Length > 0 ? batch[0] : ReadOnlyMemory<float>.Empty;
    }

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(Array.Empty<ReadOnlyMemory<float>>());
        if (!IsAvailable) return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(new ReadOnlyMemory<float>[texts.Count]);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(RunBatch(texts));
    }

    private ReadOnlyMemory<float>[] RunBatch(IReadOnlyList<string> texts)
    {
        var results = new ReadOnlyMemory<float>[texts.Count];
        var nonEmptyIndices = new List<int>(texts.Count);
        var encoded = new List<long[]>(texts.Count);
        int maxLen = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i])) { results[i] = ReadOnlyMemory<float>.Empty; continue; }
            var ids = _tokenizer!.EncodeToIds(texts[i], MaxSequenceLength - 2, out _, out _);
            var seq = new long[Math.Min(ids.Count + 2, MaxSequenceLength)];
            seq[0] = _clsTokenId;
            for (int j = 0; j < seq.Length - 2; j++) seq[j + 1] = ids[j];
            seq[^1] = _sepTokenId;
            maxLen = Math.Max(maxLen, seq.Length);
            nonEmptyIndices.Add(i);
            encoded.Add(seq);
        }
        if (nonEmptyIndices.Count == 0) return results;

        int batchSize = nonEmptyIndices.Count;
        var inputIds = new DenseTensor<long>([batchSize, maxLen]);
        var attentionMask = new DenseTensor<long>([batchSize, maxLen]);
        for (int b = 0; b < batchSize; b++)
            for (int t = 0; t < encoded[b].Length; t++) { inputIds[b, t] = encoded[b][t]; attentionMask[b, t] = 1L; }

        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, attentionMask)
        };
        if (_tokenTypeIdsName is not null)
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIdsName, new DenseTensor<long>([batchSize, maxLen])));

        lock (_inferenceLock)
        {
            if (_disposed || _session is null) return results;
            using var outputs = _session.Run(inputs);
            var tensor = outputs.FirstOrDefault(o => string.Equals(o.Name, _outputName, StringComparison.Ordinal))?.AsTensor<float>()
                ?? outputs.First().AsTensor<float>();
            var dims = tensor.Dimensions;
            if (dims.Length == 2)
            {
                for (int b = 0; b < batchSize; b++)
                {
                    var vec = new float[dims[1]];
                    for (int h = 0; h < dims[1]; h++) vec[h] = tensor[b, h];
                    results[nonEmptyIndices[b]] = Normalize(vec);
                }
            }
            else if (dims.Length == 3)
            {
                int seqLen = dims[1], hidden = dims[2];
                for (int b = 0; b < batchSize; b++)
                {
                    var vec = new float[hidden];
                    int activeLen = Math.Min(encoded[b].Length, seqLen);
                    for (int t = 0; t < activeLen; t++)
                        for (int h = 0; h < hidden; h++) vec[h] += tensor[b, t, h];
                    if (activeLen > 0) TensorPrimitives.Divide(vec, activeLen, vec);
                    results[nonEmptyIndices[b]] = Normalize(vec);
                }
            }
        }
        return results;
    }

    private static ReadOnlyMemory<float> Normalize(float[] vec)
    {
        float norm = TensorPrimitives.Norm(vec);
        if (norm <= 1e-12f) return ReadOnlyMemory<float>.Empty;
        TensorPrimitives.Divide(vec, norm, vec);
        return vec;
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_inferenceLock)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
        }
    }
}
