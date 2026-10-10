namespace Autheris.Domain.Options;

using System;
using System.ComponentModel.DataAnnotations;

public sealed class CatalogSearchOptions
{
    public const string SectionName = "Gateway:CatalogSearch";

    /// <summary>Aktiviert die Hybrid-Suche im MCP-Server und Catalog-Endpoint (Default: true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Embedding-Provider: "LocalOnnx" (Standard), "Ollama", "Disabled".</summary>
    [Required]
    public string EmbeddingProvider { get; set; } = "LocalOnnx";

    /// <summary>Lokaler Modellname für den Tokenizer/Embedder.</summary>
    public string ModelName { get; set; } = "all-MiniLM-L6-v2";

    /// <summary>Optionale Basis-URL für On-Premises Ollama Service (z. B. http://ollama:11434).</summary>
    public string? OllamaBaseUrl { get; set; } = null;

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
}
