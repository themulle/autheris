namespace Autheris.Modules.Lineage.Domain;

/// <summary>
/// Configuration options for the OpenLineage export module (<c>Gateway:Lineage</c>).
/// </summary>
public sealed class LineageModuleOptions
{
    /// <summary>Configuration section claimed by the lineage module.</summary>
    public const string SectionName = "Gateway:Lineage";

    /// <summary>Target OpenLineage HTTP ingestion endpoint URI.</summary>
    public string OpenLineageEndpoint { get; init; } = string.Empty;

    /// <summary>Optional Bearer API key for the OpenLineage endpoint.</summary>
    public string OpenLineageApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Returns a secret-free summary. Never emits <see cref="OpenLineageApiKey"/> or endpoint details.
    /// </summary>
    public override string ToString()
    {
        var hasEndpoint = !string.IsNullOrWhiteSpace(OpenLineageEndpoint);
        var hasApiKey = !string.IsNullOrWhiteSpace(OpenLineageApiKey);
        return $"LineageModuleOptions {{ HasEndpoint = {hasEndpoint}, HasApiKey = {hasApiKey} }}";
    }
}
