namespace Autheris.Infrastructure.Persistence.Catalog;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

internal class CatalogLocalizationPersistence
{
    public static LocalizedTexts ParseLocalizedTexts(string? json, string targetId, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return LocalizedTexts.Empty;
        }

        try
        {
            var dict = JsonSerializer.Deserialize(json, CatalogLocalizationJsonContext.Default.DictionaryStringLocalizedTextEntry);
            return dict == null ? LocalizedTexts.Empty : new LocalizedTexts(dict);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            // SEC-CAT146-05: Log target ID and ex.GetType().Name only, do NOT log raw JSON payload bytes or ex.Message
            logger?.LogWarning("Corrupt texts_json for {TargetId}: {ExceptionType}", targetId, ex.GetType().Name);
            return LocalizedTexts.Empty;
        }
    }

    public static LocalizedTexts ParseLocalizedTexts(string? json, Guid targetId, ILogger? logger)
        => ParseLocalizedTexts(json, targetId.ToString("D"), logger);

    public static string? SerializeLocalizedTexts(LocalizedTexts? texts)
    {
        if (texts == null || texts.IsEmpty)
        {
            return null;
        }

        var dict = new Dictionary<string, LocalizedTextEntry>(texts.Entries);
        return JsonSerializer.Serialize(dict, CatalogLocalizationJsonContext.Default.DictionaryStringLocalizedTextEntry);
    }
}
