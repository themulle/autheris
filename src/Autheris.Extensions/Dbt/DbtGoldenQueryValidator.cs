namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using HotChocolate.Language;

/// <summary>
/// Validates and sanitises one raw dbt <c>meta.golden_queries</c> entry. Returns either a store entry or a reason code; the reason code
/// never contains a value from the entry.
/// </summary>
internal static class DbtGoldenQueryValidator
{
    internal const int MaxEntriesPerDataset = 20;
    internal const int MaxTitleLength = 256;
    internal const int MaxDescriptionLength = 2048;
    internal const int MaxQueryBytes = 16 * 1024;
    internal const int MaxVariablesBytes = 4 * 1024;
    internal const int MaxVariablesDepth = 3;
    internal const int MaxSelectionDepth = 10;
    internal const int MaxSelectedFields = 200;

    public static (GoldenQueryStoreEntry? Entry, string? Reason) Validate(DbtGoldenQueryRawEntry raw, TableIdentifier dataset, string tableId)
    {
        if (raw.ShapeReason is not null)
        {
            return (null, raw.ShapeReason);
        }

        if (string.IsNullOrWhiteSpace(raw.Query))
        {
            return (null, "QUERY_EMPTY");
        }

        if (Encoding.UTF8.GetByteCount(raw.Query) > MaxQueryBytes)
        {
            return (null, "QUERY_TOO_LARGE");
        }

        if (string.IsNullOrWhiteSpace(raw.Title))
        {
            return (null, "TITLE_EMPTY");
        }

        if (raw.Title.Length > MaxTitleLength)
        {
            return (null, "TITLE_TOO_LONG");
        }

        if (raw.Description is { Length: > MaxDescriptionLength })
        {
            return (null, "DESCRIPTION_TOO_LONG");
        }

        if (raw.VariablesJson is not null && Encoding.UTF8.GetByteCount(raw.VariablesJson) > MaxVariablesBytes)
        {
            return (null, "VARIABLES_TOO_LARGE");
        }

        var title = DiscoveryTextSanitizer.SanitizeText(raw.Title, MaxTitleLength);
        if (string.IsNullOrWhiteSpace(title))
        {
            return (null, "TITLE_EMPTY");
        }

        var description = DiscoveryTextSanitizer.SanitizeText(raw.Description, MaxDescriptionLength, allowNewlinesAndTabs: true);
        var query = DiscoveryTextSanitizer.SanitizeText(raw.Query, MaxQueryBytes, allowNewlinesAndTabs: true);
        if (string.IsNullOrWhiteSpace(query))
        {
            return (null, "QUERY_EMPTY");
        }

        var astReason = ValidateQueryAst(query, dataset);
        if (astReason is not null)
        {
            return (null, astReason);
        }

        string? variables = null;
        if (raw.VariablesJson is not null)
        {
            (variables, var variablesReason) = SanitizeVariables(raw.VariablesJson);
            if (variablesReason is not null)
            {
                return (null, variablesReason);
            }
        }

        var sourceKey = "dbt-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tableId + "\n" + title.ToLowerInvariant())))[..24];
        return (new GoldenQueryStoreEntry(sourceKey, title, string.IsNullOrWhiteSpace(description) ? null : description, query, variables), null);
    }

    /// <summary>A root field belongs to the dataset when it is the table name or the catalog GraphQL name (<c>domain_schema_table</c>), optionally as connection field.</summary>
    internal static bool OwnsRootField(TableIdentifier dataset, string field)
    {
        var name = field.EndsWith("Connection", StringComparison.Ordinal) ? field[..^"Connection".Length] : field;
        return string.Equals(name, dataset.TableName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Sanitize($"{dataset.Domain}_{dataset.Schema}_{dataset.TableName}"), StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Sanitize($"{dataset.Schema}_{dataset.TableName}"), StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string input) =>
        string.Create(input.Length, input, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_';
            }
        });

    private static string? ValidateQueryAst(string query, TableIdentifier dataset)
    {
        DocumentNode document;
        try
        {
            document = Utf8GraphQLParser.Parse(query);
        }
        catch (Exception ex) when (ex is SyntaxException or ArgumentException or InvalidOperationException)
        {
            return "QUERY_SYNTAX";
        }

        var operations = document.Definitions.OfType<OperationDefinitionNode>().ToList();
        if (operations.Count == 0 || document.Definitions.Any(d => d is not OperationDefinitionNode and not FragmentDefinitionNode))
        {
            return "QUERY_SYNTAX";
        }

        var fields = 0;
        foreach (var operation in operations)
        {
            if (operation.Operation != OperationType.Query)
            {
                return "QUERY_NOT_QUERY_OPERATION";
            }

            foreach (var selection in operation.SelectionSet.Selections)
            {
                if (selection is not FieldNode root)
                {
                    return "QUERY_ROOT_NOT_FIELD";
                }

                if (!OwnsRootField(dataset, root.Name.Value))
                {
                    return "QUERY_FOREIGN_DATASET";
                }
            }

            if (!WithinBounds(operation.SelectionSet, 1, ref fields))
            {
                return "QUERY_TOO_COMPLEX";
            }
        }

        return null;
    }

    private static bool WithinBounds(SelectionSetNode? set, int depth, ref int fields)
    {
        if (set is null)
        {
            return true;
        }

        if (depth > MaxSelectionDepth)
        {
            return false;
        }

        foreach (var selection in set.Selections)
        {
            if (++fields > MaxSelectedFields)
            {
                return false;
            }

            var child = selection switch
            {
                FieldNode f => f.SelectionSet,
                InlineFragmentNode i => i.SelectionSet,
                _ => null
            };
            if (!WithinBounds(child, depth + 1, ref fields))
            {
                return false;
            }
        }

        return true;
    }

    private static (string? Json, string? Reason) SanitizeVariables(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = MaxVariablesDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "VARIABLES_INVALID");
            }

            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteSanitized(document.RootElement, writer);
            }

            var json = Encoding.UTF8.GetString(stream.ToArray());
            return Encoding.UTF8.GetByteCount(json) > MaxVariablesBytes ? (null, "VARIABLES_TOO_LARGE") : (json, null);
        }
        catch (JsonException)
        {
            return (null, "VARIABLES_INVALID");
        }
    }

    private static void WriteSanitized(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(DiscoveryTextSanitizer.SanitizeText(property.Name, MaxTitleLength) ?? string.Empty);
                    WriteSanitized(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSanitized(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(DiscoveryTextSanitizer.SanitizeText(element.GetString(), MaxDescriptionLength) ?? string.Empty);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
