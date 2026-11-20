namespace Autheris.Domain.Options;

using System.Collections.Generic;

/// <summary>Catalog metadata localization settings (<c>Gateway:Catalog</c>).</summary>
public sealed class CatalogLocalizationOptions
{
    public const string SectionName = "Gateway:Catalog";

    /// <summary>Default fallback language code used when a localized catalog description is unavailable.</summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>List of supported ISO language codes for catalog metadata localization.</summary>
    public IReadOnlyList<string> SupportedLanguages { get; set; } = ["en", "de"];
}
