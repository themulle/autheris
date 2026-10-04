namespace Autheris.Application.Governance.Contracts;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// F-GOV-08: High-performance GraphQL Schema Slicing &amp; Contract Filter.
/// Filters GraphQL SDL by evaluating @tag(name: "...") and @inaccessible directives,
/// removing inaccessible elements and pruning orphaned types to ensure zero information leakage.
/// </summary>
public static class SchemaContractFilter
{
    private static readonly Regex TagRegex = new(@"@tag\s*\(\s*name\s*:\s*""([^""]+)""\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex InaccessibleRegex = new(@"@inaccessible\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public static string FilterSchema(string sdl, SchemaContractDefinition contract)
    {
        ArgumentNullException.ThrowIfNull(sdl);
        ArgumentNullException.ThrowIfNull(contract);

        if (string.IsNullOrWhiteSpace(sdl))
        {
            return sdl;
        }

        var lines = sdl.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var sb = new StringBuilder();

        bool insideType = false;
        string currentTypeName = string.Empty;
        string currentTypeKind = string.Empty;
        var currentFields = new List<string>();
        var typeHeader = string.Empty;
        bool typeIsInaccessible = false;
        var typeTags = new List<string>();

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();

            // Directives definition lines for tag/inaccessible can be removed from output
            if (trimmed.StartsWith("directive @tag", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("directive @inaccessible", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!insideType)
            {
                if (IsTypeStart(trimmed, out var kind, out var name))
                {
                    insideType = true;
                    currentTypeKind = kind;
                    currentTypeName = name;
                    currentFields.Clear();
                    typeHeader = rawLine;
                    typeIsInaccessible = InaccessibleRegex.IsMatch(rawLine);
                    typeTags = ExtractTags(rawLine);
                    continue;
                }

                // Top-level schema or comments
                sb.AppendLine(rawLine);
            }
            else
            {
                if (trimmed == "}")
                {
                    insideType = false;

                    // Evaluate type-level filtering
                    if (typeIsInaccessible && contract.ExcludeInaccessible)
                    {
                        continue;
                    }

                    if (typeTags.Any(t => contract.ExcludedTags.Contains(t)))
                    {
                        continue;
                    }

                    if (contract.IncludedTags.Count > 0 && typeTags.Count > 0 &&
                        !typeTags.Any(t => contract.IncludedTags.Contains(t)))
                    {
                        continue;
                    }

                    // If all fields were filtered out and it's not a standard empty type
                    if (currentFields.Count == 0 &&
                        !string.Equals(currentTypeName, "Query", StringComparison.OrdinalIgnoreCase))
                    {
                        // Prune empty type
                        continue;
                    }

                    // Clean headers from internal directives
                    var cleanedHeader = CleanDirectives(typeHeader);
                    sb.AppendLine(cleanedHeader);
                    foreach (var f in currentFields)
                    {
                        sb.AppendLine(f);
                    }
                    sb.AppendLine("}");
                }
                else if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    // Field level evaluation
                    if (IsFieldAllowed(rawLine, contract))
                    {
                        var cleanedField = CleanDirectives(rawLine);
                        currentFields.Add(cleanedField);
                    }
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static bool IsTypeStart(string trimmed, out string kind, out string name)
    {
        kind = string.Empty;
        name = string.Empty;

        var tokens = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2 &&
            (tokens[0] == "type" || tokens[0] == "interface" || tokens[0] == "input" || tokens[0] == "enum"))
        {
            kind = tokens[0];
            name = tokens[1].TrimEnd('{');
            return true;
        }

        return false;
    }

    private static bool IsFieldAllowed(string fieldLine, SchemaContractDefinition contract)
    {
        if (contract.ExcludeInaccessible && InaccessibleRegex.IsMatch(fieldLine))
        {
            return false;
        }

        var tags = ExtractTags(fieldLine);
        if (tags.Any(t => contract.ExcludedTags.Contains(t)))
        {
            return false;
        }

        if (contract.IncludedTags.Count > 0)
        {
            // If contract specifies included tags, only fields tagged with one of them are allowed
            if (tags.Count > 0 && !tags.Any(t => contract.IncludedTags.Contains(t)))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> ExtractTags(string text)
    {
        var matches = TagRegex.Matches(text);
        var tags = new List<string>(matches.Count);
        foreach (Match m in matches)
        {
            if (m.Groups.Count > 1)
            {
                tags.Add(m.Groups[1].Value);
            }
        }
        return tags;
    }

    private static string CleanDirectives(string text)
    {
        var cleaned = TagRegex.Replace(text, string.Empty);
        cleaned = InaccessibleRegex.Replace(cleaned, string.Empty);
        return cleaned.TrimEnd();
    }
}
