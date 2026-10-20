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

    /// <summary>Aktiviert die Suche im MCP-Server und Catalog-Endpoint (Default: true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Embedding-Provider: None (Default), LocalOnnx, Ollama.</summary>
    public CatalogEmbeddingProvider EmbeddingProvider { get; set; } = CatalogEmbeddingProvider.None;

    // LocalOnnx Settings
    public string? ModelPath { get; set; }
    public string? TokenizerPath { get; set; }
    public string? ModelSha256 { get; set; }

    // Ollama Settings
    public string? OllamaEndpoint { get; set; }
    public string? OllamaModelName { get; set; }
    public bool OllamaInsecureAllowHttp { get; set; } = false;

    // Ranking & Search Settings
    public int RefreshSeconds { get; set; } = 15;

    /// <summary>RRF-Glättungskonstante k (Standard: 60 gemäß Cormack et al.).</summary>
    [Range(1, 200)]
    public int RrfConstantK { get; set; } = 60;

    /// <summary>Standard-Trefferanzahl für MCP-Suchaufrufe.</summary>
    [Range(1, 50)]
    public int DefaultLimit { get; set; } = 5;

    /// <summary>Automatischer asynchroner Index-Warmup beim Anwendungsstart.</summary>
    public bool WarmupOnStartup { get; set; } = true;

    /// <summary>Schwellenwert für minimale Kosinus-Ähnlichkeit bei reiner Vektorsuche.</summary>
    public float MinVectorSimilarity { get; set; } = 0.25f;

    /// <summary>Maximale Abfragelänge in Zeichen zur Abwehr von DoS-Attacken.</summary>
    public int MaxQueryLength { get; set; } = 256;

    /// <summary>Gewichtungen für einzelne Metadaten-Felder.</summary>
    public CatalogSearchFieldWeights FieldWeights { get; set; } = new();

    // Legacy/compatibility properties
    public int EmbeddingDimensions { get; set; } = 384;
    public string ModelName { get; set; } = "all-MiniLM-L6-v2";
    public string? LocalOnnxModelPath { get => ModelPath; set => ModelPath = value; }
    public string? LocalOnnxModelSha256 { get => ModelSha256; set => ModelSha256 = value; }
    public string? OllamaModel { get => OllamaModelName; set => OllamaModelName = value; }
    public string? OllamaBaseUrl { get => OllamaEndpoint; set => OllamaEndpoint = value; }
}

public sealed class CatalogSearchFieldWeights
{
    public double TechnicalName { get; set; } = 10.0;
    public double DisplayName { get; set; } = 8.0;
    public double TagsAndOwner { get; set; } = 5.0;
    public double ShortDescription { get; set; } = 3.0;
    public double LongDescription { get; set; } = 2.0;
    public double ColumnNames { get; set; } = 2.0;
    public double ColumnDescriptions { get; set; } = 1.0;
}
