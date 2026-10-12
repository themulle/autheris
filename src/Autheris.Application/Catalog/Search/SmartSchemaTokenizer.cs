namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Intelligenter Tokenizer für Datenkatalog- und Datenbankschema-Metadaten.
/// Behandelt Casing-Konventionen (snake_case, camelCase, PascalCase) sowie
/// domänenspezifische Abkürzungen und bilinguale Stopwörter.
/// </summary>
public static class SmartSchemaTokenizer
{
    private static readonly Regex CasingSplitRegex = new(@"[_\-\.\s/]+|(?<!^)(?=[A-Z][a-z])|(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);

    // Domänen-spezifisches Wörterbuch für automatische Synonym-Expansion
    private static readonly Dictionary<string, string[]> AbbreviationMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cust"] = ["customer", "kunde"],
        ["kna1"] = ["customer", "kunde", "debitor", "stammdaten"],
        ["vbak"] = ["sales", "auftrag", "order", "verkaufsbeleg"],
        ["vbap"] = ["sales", "item", "position", "auftragsposition"],
        ["inv"] = ["invoice", "rechnung", "faktura"],
        ["rev"] = ["revenue", "umsatz", "erloes"],
        ["fct"] = ["fact", "fakten"],
        ["dim"] = ["dimension", "stammdaten"],
        ["tx"] = ["transaction", "transaktion", "buchung"],
        ["addr"] = ["address", "adresse", "anschrift"],
        ["usr"] = ["user", "benutzer", "anwender"],
        ["prod"] = ["product", "produkt", "artikel"],
        ["emp"] = ["employee", "mitarbeiter", "angestellter"],
        ["storn"] = ["storno", "cancellation", "widerruf"],
        ["sal"] = ["salary", "gehalt", "lohn"]
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "for", "with", "in", "on", "at", "by", "of", "to",
        "der", "die", "das", "und", "oder", "fuer", "mit", "in", "von", "zu", "im", "am", "des", "den"
    };

    public static string[] Tokenize(string? text, bool expandAbbreviations = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var rawTokens = CasingSplitRegex.Split(text)
            .Where(t => t.Length >= 2)
            .Select(t => t.ToLowerInvariant())
            .Where(t => !StopWords.Contains(t))
            .ToList();

        if (!expandAbbreviations) return rawTokens.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var expanded = new List<string>(rawTokens.Count * 2);
        foreach (var token in rawTokens)
        {
            expanded.Add(token);
            if (AbbreviationMap.TryGetValue(token, out var synonyms))
            {
                expanded.AddRange(synonyms);
            }
        }

        return expanded.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
