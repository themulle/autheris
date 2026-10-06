namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;
using HotChocolate.Language;

/// <summary>
/// SEC A-4: Derives the set of tables touched by a curated GraphQL operation from its AST so that ABAC and the
/// four-eyes gate run on the real tables instead of a guessed first root field.
/// </summary>
internal static class McpGraphQlTableResolver
{
    /// <summary>
    /// Returns the tables of all root fields (fragments expanded). Root fields named <c>table</c> must carry literal
    /// <c>domain</c> and <c>name</c> (optional <c>schema</c>) arguments; other root fields map to <c>default.dbo.&lt;field&gt;</c>.
    /// Returns null (fail-closed) when the document cannot be parsed, has no root field or uses variables for table arguments.
    /// </summary>
    public static IReadOnlyList<TableIdentifier>? ResolveTables(string document)
    {
        DocumentNode doc;
        try
        {
            doc = Utf8GraphQLParser.Parse(document);
        }
        catch (Exception)
        {
            return null;
        }

        var fragments = doc.Definitions.OfType<FragmentDefinitionNode>().ToDictionary(f => f.Name.Value, StringComparer.Ordinal);
        var tables = new List<TableIdentifier>();
        var operations = doc.Definitions.OfType<OperationDefinitionNode>().ToList();
        if (operations.Count == 0)
        {
            return null;
        }

        foreach (var op in operations)
        {
            if (!Collect(op.SelectionSet, fragments, tables, 0))
            {
                return null;
            }
        }

        return tables.Count == 0 ? null : tables.Distinct().ToList();
    }

    private static bool Collect(
        SelectionSetNode set,
        Dictionary<string, FragmentDefinitionNode> fragments,
        List<TableIdentifier> tables,
        int depth)
    {
        if (depth > 16)
        {
            return false;
        }

        foreach (var selection in set.Selections)
        {
            switch (selection)
            {
                case FieldNode field:
                    var name = field.Name.Value;
                    if (name.StartsWith("__", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (name.Equals("table", StringComparison.OrdinalIgnoreCase))
                    {
                        var id = FromTableArguments(field);
                        if (id == null)
                        {
                            return false;
                        }

                        tables.Add(id.Value);
                    }
                    else
                    {
                        tables.Add(new TableIdentifier("default", "dbo", name.ToLowerInvariant()));
                    }

                    break;
                case InlineFragmentNode inline:
                    if (!Collect(inline.SelectionSet, fragments, tables, depth + 1))
                    {
                        return false;
                    }

                    break;
                case FragmentSpreadNode spread:
                    if (!fragments.TryGetValue(spread.Name.Value, out var frag) ||
                        !Collect(frag.SelectionSet, fragments, tables, depth + 1))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static TableIdentifier? FromTableArguments(FieldNode field)
    {
        string? Get(string arg) =>
            field.Arguments.FirstOrDefault(a => a.Name.Value.Equals(arg, StringComparison.OrdinalIgnoreCase))?.Value is StringValueNode sv
                && !string.IsNullOrWhiteSpace(sv.Value) ? sv.Value : null;

        var domain = Get("domain");
        var name = Get("name");
        if (domain == null || name == null)
        {
            return null;
        }

        return new TableIdentifier(domain, Get("schema") ?? "dbo", name);
    }
}
