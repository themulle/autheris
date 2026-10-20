namespace Autheris.Application.Catalog.Localization;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class CatalogLocalizationOptionsValidator : IValidateOptions<CatalogLocalizationOptions>
{
    public ValidateOptionsResult Validate(string? name, CatalogLocalizationOptions options)
    {
        if (options == null)
            return ValidateOptionsResult.Fail("CatalogLocalizationOptions cannot be null.");

        if (string.IsNullOrWhiteSpace(options.DefaultLanguage))
            return ValidateOptionsResult.Fail("DefaultLanguage must not be empty.");

        if (!Bcp47LanguageTag.IsValid(options.DefaultLanguage, out var canonicalDefault))
            return ValidateOptionsResult.Fail($"DefaultLanguage '{options.DefaultLanguage}' is not a valid BCP 47 language tag.");

        if (options.SupportedLanguages == null || options.SupportedLanguages.Count == 0 || options.SupportedLanguages.Count > 16)
            return ValidateOptionsResult.Fail("SupportedLanguages must contain between 1 and 16 entries.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in options.SupportedLanguages)
        {
            if (!Bcp47LanguageTag.IsValid(lang, out var canonicalTag))
                return ValidateOptionsResult.Fail($"SupportedLanguage '{lang}' is not a valid BCP 47 language tag.");
            if (!seen.Add(canonicalTag))
                return ValidateOptionsResult.Fail($"Duplicate language tag '{lang}' in SupportedLanguages.");
        }

        if (!seen.Contains(canonicalDefault))
            return ValidateOptionsResult.Fail($"DefaultLanguage '{options.DefaultLanguage}' must be present in SupportedLanguages.");

        return ValidateOptionsResult.Success;
    }
}
