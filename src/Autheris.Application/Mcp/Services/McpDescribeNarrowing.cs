namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autheris.Domain.Model;

/// <summary>
/// Narrows an already authorized <c>describe_dataset</c> result to the sections, columns and relations an agent asked for.
/// It only removes members: the filters never widen what the caller may see, and every value is checked against an allow-list.
/// </summary>
public static class McpDescribeNarrowing
{
    public const string SectionColumns = "columns";
    public const string SectionKeys = "keys";
    public const string SectionIndexes = "indexes";
    public const string SectionRelations = "relations";
    public const string SectionExamples = "examples";
    public const string SectionGraphQl = "graphql";
    public const string SectionAccess = "access";

    /// <summary>Serves the long description of each returned column; absent by default.</summary>
    public const string SectionLongDescription = "longDescription";

    /// <summary>Most columns one answer may carry when the caller pages through the columns.</summary>
    public const int MaxColumnLimit = 500;

    /// <summary>Most names one <c>columns</c> or <c>include</c> argument may list.</summary>
    public const int MaxListedNames = 200;

    private static readonly HashSet<string> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        SectionColumns, SectionKeys, SectionIndexes, SectionRelations, SectionExamples, SectionGraphQl, SectionAccess, SectionLongDescription
    };

    /// <summary>Parsed narrowing request; every member is optional.</summary>
    public sealed record Request(
        IReadOnlySet<string>? Include,
        IReadOnlyList<string>? Columns,
        int ColumnOffset,
        int? ColumnLimit,
        string? RelationTerm)
    {
        public bool IsEmpty => Include is null && Columns is null && ColumnOffset == 0 && ColumnLimit is null && RelationTerm is null;
    }

    /// <summary>
    /// Reads the narrowing arguments. Returns false (invalid parameters) for an unknown <c>include</c> value, a malformed list,
    /// a negative offset or a limit outside 1..<see cref="MaxColumnLimit"/>.
    /// </summary>
    public static bool TryParse(object? include, object? columns, int? columnOffset, int? columnLimit, object? relations, out Request request)
    {
        request = new Request(null, null, 0, null, null);

        HashSet<string>? includeSet = null;
        if (include is not null)
        {
            if (!TryReadNames(include, out var names))
            {
                return false;
            }

            includeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (!Sections.Contains(name))
                {
                    return false;
                }

                includeSet.Add(name);
            }
        }

        IReadOnlyList<string>? columnNames = null;
        if (columns is not null)
        {
            if (!TryReadNames(columns, out var names))
            {
                return false;
            }

            columnNames = names;
        }

        if (columnOffset is < 0)
        {
            return false;
        }

        if (columnLimit is { } limit && (limit < 1 || limit > MaxColumnLimit))
        {
            return false;
        }

        string? term = null;
        if (relations is not null)
        {
            if (relations is not string text)
            {
                return false;
            }

            term = text.Trim();
            if (term.Length == 0 || term.Length > 200)
            {
                return false;
            }
        }

        request = new Request(includeSet, columnNames, columnOffset ?? 0, columnLimit, term);
        return true;
    }

    /// <summary>Applies the request to an authorized description. A request without members returns the description unchanged.</summary>
    public static McpDatasetDescription Apply(McpDatasetDescription description, Request request)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Include?.Contains(SectionLongDescription) != true && description.Columns.Any(static c => c.LongDescription is not null))
        {
            description = description with { Columns = description.Columns.Select(static c => c with { LongDescription = null }).ToList() };
        }

        if (request.IsEmpty)
        {
            return description;
        }

        var result = description;

        // Column selection: explicit names win over paging; both only remove columns.
        var columnsTotal = description.Columns.Count;
        IReadOnlyList<McpDatasetColumn> columns = description.Columns;
        var narrowedColumns = false;
        if (request.Columns is { } names)
        {
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            columns = columns.Where(c => wanted.Contains(c.Name)).ToList();
            narrowedColumns = true;
        }
        else if (request.ColumnOffset > 0 || request.ColumnLimit is not null)
        {
            columns = columns.Skip(request.ColumnOffset).Take(request.ColumnLimit ?? MaxColumnLimit).ToList();
            narrowedColumns = true;
        }

        if (request.Include is { } include && !include.Contains(SectionColumns))
        {
            columns = [];
            narrowedColumns = true;
        }

        if (narrowedColumns)
        {
            result = result with { Columns = columns, ColumnTotal = columnsTotal };
        }

        var graphQl = result.GraphQl;
        if (graphQl is not null && request.RelationTerm is { } term)
        {
            graphQl = graphQl with
            {
                Relations = graphQl.Relations
                    .Where(r => r.Field.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || r.Dataset.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            };
        }

        if (request.Include is { } sections)
        {
            if (!sections.Contains(SectionGraphQl))
            {
                graphQl = null;
            }
            else if (graphQl is not null && !sections.Contains(SectionRelations))
            {
                graphQl = graphQl with { Relations = [] };
            }

            result = result with
            {
                Examples = sections.Contains(SectionExamples) ? result.Examples : [],
                Indexes = sections.Contains(SectionIndexes) ? result.Indexes : null,
                PrimaryKeyOrder = sections.Contains(SectionKeys) ? result.PrimaryKeyOrder : null,
                OtherAccess = sections.Contains(SectionAccess) ? result.OtherAccess : null
            };
        }

        return result with { GraphQl = graphQl };
    }

    private static bool TryReadNames(object value, out IReadOnlyList<string> names)
    {
        names = [];
        if (value is not string text)
        {
            return false;
        }

        var trimmed = text.Trim();
        var list = new List<string>();
        if (trimmed.StartsWith('[', StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } s)
                    {
                        return false;
                    }

                    list.Add(s.Trim());
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }
        else
        {
            foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                list.Add(part);
            }
        }

        if (list.Count > MaxListedNames)
        {
            return false;
        }

        names = list;
        return true;
    }
}
