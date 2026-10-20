namespace Autheris.Domain.Options;

using System.Collections.Generic;

public sealed class CatalogLocalizationOptions
{
    public const string SectionName = "Gateway:Catalog";

    public string DefaultLanguage { get; set; } = "en";
    public IReadOnlyList<string> SupportedLanguages { get; set; } = ["en", "de"];
}
