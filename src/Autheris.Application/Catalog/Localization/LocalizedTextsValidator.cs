namespace Autheris.Application.Catalog.Localization;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class LocalizedTextsValidationException : ValidationException
{
    public LocalizedTextsValidationException(string message) : base(message) { }
}

/// <summary>
/// Write-side and ingestion validator for localized catalog texts.
/// Enforces character whitelisting, script tag rejection, and field length caps.
/// Fails closed without echoing untrusted input payloads in error messages (SEC-CAT146-03, T-CAT146-04).
/// </summary>
public sealed class LocalizedTextsValidator
{
    public const int MaxLanguagesPerEntity = 16;
    public const int MaxDisplayNameLength = 128;
    public const int MaxDescriptionLength = 4000;
    public const int MaxLongDescriptionLength = 32000;

    private readonly string? _defaultLanguage;
    private readonly HashSet<string>? _supportedLanguages;

    public LocalizedTextsValidator() : this(null) { }

    public LocalizedTextsValidator(IOptions<CatalogLocalizationOptions>? options)
    {
        if (options?.Value != null)
        {
            var opts = options.Value;
            if (Bcp47LanguageTag.IsValid(opts.DefaultLanguage, out var canonicalDefault))
            {
                _defaultLanguage = canonicalDefault;
            }

            if (opts.SupportedLanguages != null && opts.SupportedLanguages.Count > 0)
            {
                _supportedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var lang in opts.SupportedLanguages)
                {
                    if (Bcp47LanguageTag.IsValid(lang, out var canonical))
                    {
                        _supportedLanguages.Add(canonical);
                    }
                }
                if (_defaultLanguage != null)
                {
                    _supportedLanguages.Add(_defaultLanguage);
                }
            }
        }
    }

    public static void Validate(LocalizedTexts? texts, string entityContext = "entity")
    {
        if (texts == null || texts.IsEmpty) return;

        if (texts.Entries.Count > MaxLanguagesPerEntity)
        {
            throw new LocalizedTextsValidationException(
                $"Localization validation failed for {entityContext}: maximum of {MaxLanguagesPerEntity} language entries exceeded.");
        }

        foreach (var (lang, entry) in texts.Entries)
        {
            ValidateEntryDirect(lang, entry, entityContext);
        }
    }

    public void ValidateInstance(LocalizedTexts? texts, string entityContext = "entity")
    {
        if (texts == null || texts.IsEmpty) return;

        if (texts.Entries.Count > MaxLanguagesPerEntity)
        {
            throw new LocalizedTextsValidationException(
                $"Localization validation failed for {entityContext}: maximum of {MaxLanguagesPerEntity} language entries exceeded.");
        }

        foreach (var (lang, entry) in texts.Entries)
        {
            ValidateEntry(lang, entry, entityContext);
        }
    }

    public void ValidateEntry(string languageTag, LocalizedTextEntry? entry, string entityContext = "entity")
    {
        var canonicalTag = ValidateEntryDirect(languageTag, entry, entityContext);

        if (_defaultLanguage != null && string.Equals(canonicalTag, _defaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalizedTextsValidationException(
                $"Localization validation failed for {entityContext}: language tag matches default language '{_defaultLanguage}'. Default language texts must be stored in primary columns (R-146-4).");
        }

        if (_supportedLanguages != null && !_supportedLanguages.Contains(canonicalTag))
        {
            throw new LocalizedTextsValidationException(
                $"Localization validation failed for {entityContext}: language tag is not present in configured supported languages.");
        }
    }

    private static string ValidateEntryDirect(string languageTag, LocalizedTextEntry? entry, string entityContext)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            throw new LocalizedTextsValidationException(
                $"Invalid BCP 47 language tag: language tag must not be empty for {entityContext}.");
        }

        var trimmedTag = languageTag.Trim();
        if (trimmedTag.Length > Bcp47LanguageTag.MaxTagLength || !Bcp47LanguageTag.IsValid(trimmedTag, out var canonicalTag))
        {
            throw new LocalizedTextsValidationException(
                $"Invalid BCP 47 language tag: '{languageTag}' for {entityContext}.");
        }

        if (entry != null)
        {
            ValidateDisplayName(entry.DisplayName, canonicalTag, entityContext);
            ValidateDescription(entry.Description, canonicalTag, entityContext);
            ValidateLongDescription(entry.LongDescription, canonicalTag, entityContext);
        }

        return canonicalTag;
    }

    private static void ValidateDisplayName(string? text, string lang, string entityContext)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (text.Length > MaxDisplayNameLength)
        {
            throw new LocalizedTextsValidationException(
                $"Title exceeds maximum length of {MaxDisplayNameLength} characters for {entityContext}.");
        }

        CheckForInjectionAndControlChars(text, "DisplayName", lang, entityContext, allowStandardWhitespace: false);
    }

    private static void ValidateDescription(string? text, string lang, string entityContext)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (text.Length > MaxDescriptionLength)
        {
            throw new LocalizedTextsValidationException(
                $"Description exceeds maximum length of {MaxDescriptionLength} characters for {entityContext}.");
        }

        CheckForInjectionAndControlChars(text, "Description", lang, entityContext, allowStandardWhitespace: true);
    }

    private static void ValidateLongDescription(string? text, string lang, string entityContext)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (text.Length > MaxLongDescriptionLength)
        {
            throw new LocalizedTextsValidationException(
                $"LongDescription exceeds maximum length of {MaxLongDescriptionLength} characters for {entityContext}.");
        }

        CheckForInjectionAndControlChars(text, "LongDescription", lang, entityContext, allowStandardWhitespace: true);
    }

    private static void CheckForInjectionAndControlChars(string text, string fieldName, string lang, string entityContext, bool allowStandardWhitespace)
    {
        // 1. Check for script / HTML tags (SEC-CAT146-03: do NOT echo payload in exception message)
        if (ContainsHtmlTags(text))
        {
            throw new LocalizedTextsValidationException(
                $"HTML tags are not permitted in localized text for {entityContext} {fieldName}.");
        }

        // 2. Check for BiDi overrides (U+202A..U+202E, U+2066..U+2069)
        if (ContainsBiDiControls(text))
        {
            throw new LocalizedTextsValidationException(
                $"BiDi control characters are not permitted in localized text for {entityContext} {fieldName}.");
        }

        // 3. Check for disallowed control characters
        if (ContainsDisallowedControlChars(text, allowStandardWhitespace))
        {
            throw new LocalizedTextsValidationException(
                $"Control characters are not permitted in localized text for {entityContext} {fieldName}.");
        }
    }

    private static bool ContainsHtmlTags(string text)
    {
        if (text.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int idx = 0;
        while ((idx = text.IndexOf('<', idx)) != -1)
        {
            int next = idx + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            if (next < text.Length && text[next] == '/')
            {
                next++;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            }

            if (next < text.Length && char.IsAsciiLetter(text[next]))
            {
                return true;
            }

            idx++;
        }

        return false;
    }

    private static bool ContainsBiDiControls(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069'))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsDisallowedControlChars(string text, bool allowStandardWhitespace)
    {
        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                if (allowStandardWhitespace && (c == '\r' || c == '\n' || c == '\t'))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }
}
