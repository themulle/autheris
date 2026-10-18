namespace Autheris.Domain.Options;

/// <summary>#112: persistent semantic (dbt) manifest catalog (<c>Semantic</c> section).</summary>
public sealed class SemanticOptions
{
    public const string SectionName = "Semantic";
    public const int MaxManifestCacheSeconds = 3600;
    public const int MinFilterTemplateLength = 128;
    public const int MaxFilterTemplateLengthUpperBound = 65_536;

    /// <summary>Persistent catalog; <c>false</c> keeps the manifest in memory (tests, ephemeral).</summary>
    public bool PersistManifest { get; init; } = true;

    /// <summary>Cross-node staleness bound in seconds; 0 re-checks the store on every access.</summary>
    public int ManifestCacheSeconds { get; init; } = 30;

    /// <summary>Maximum length of a metric filter template (SEC-112-16 a).</summary>
    public int MaxFilterTemplateLength { get; init; } = 2048;
}
