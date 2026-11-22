namespace Autheris.Domain.Options;

using System;
using System.ComponentModel.DataAnnotations;

public enum CatalogEmbeddingProvider
{
    None = 0,
    LocalOnnx = 1,
    Ollama = 2
}

public sealed class CatalogSearchOptions
{
    public const string SectionName = "Gateway:CatalogSearch";

    /// <summary>Embedding-Provider: None (Default), LocalOnnx, Ollama.</summary>
    public CatalogEmbeddingProvider EmbeddingProvider { get; set; } = CatalogEmbeddingProvider.None;

    // LocalOnnx Settings
    /// <summary>Filesystem path to the local ONNX embedding model file (e.g., quantized 384-dim multilingual-e5-small or paraphrase-multilingual-MiniLM-L12-v2).</summary>
    public string? ModelPath { get; set; }

    /// <summary>Filesystem path to the tokenizer vocabulary/config file (vocab.txt or tokenizer.json) for the local ONNX model.</summary>
    public string? TokenizerPath { get; set; }

    /// <summary>Expected hex-encoded SHA-256 digest used to verify the local ONNX model file.</summary>
    public string? ModelSha256 { get; set; }

    // Ollama Settings
    /// <summary>Ollama base URL. Loopback addresses are refused; private hosts need <c>Gateway:Egress</c> trusted hosts or networks plus <c>CatalogEmbeddings</c> in <c>TrustedIntegrations</c>.</summary>
    public string? OllamaEndpoint { get; set; }

    /// <summary>Name of the Ollama embedding model to invoke (use multilingual models such as bge-m3 or snowflake-arctic-embed2 for cross-language catalog search).</summary>
    public string? OllamaModelName { get; set; }

    /// <summary>When true, permits plain HTTP connections to the configured Ollama endpoint (development/testing only).</summary>
    public bool OllamaInsecureAllowHttp { get; set; } = false;

    // Ranking & Search Settings
    /// <summary>Minimum interval in seconds between incremental catalog search index refreshes.</summary>
    public int RefreshSeconds { get; set; } = 15;

    /// <summary>RRF smoothing constant k (default: 60 per Cormack et al.).</summary>
    [Range(1, 200)]
    public int RrfConstantK { get; set; } = 60;

    /// <summary>Default hit count for MCP search calls.</summary>
    [Range(1, 50)]
    public int DefaultLimit { get; set; } = 5;

    /// <summary>Automatic asynchronous index warmup on application startup.</summary>
    public bool WarmupOnStartup { get; set; } = true;

    /// <summary>Minimum cosine similarity threshold for pure vector search.</summary>
    public float MinVectorSimilarity { get; set; } = 0.25f;

    /// <summary>Maximum query length in characters to mitigate DoS attacks.</summary>
    public int MaxQueryLength { get; set; } = 256;

    /// <summary>Dimension of the embedding vectors (LocalOnnx, Ollama; default 384 for multilingual-e5-small / MiniLM-L12-v2).</summary>
    public int EmbeddingDimensions { get; set; } = 384;
}
