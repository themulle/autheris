using System.Text.RegularExpressions;

namespace Autheris.Domain.Model;

/// <summary>Validates and normalizes the declared primary key and indexes of a table (fail closed).</summary>
public static partial class TableKeyDefinitionValidator
{
    public const int MaxColumnsPerKey = 32;
    public const int MaxIndexesPerTable = 64;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex IndexNameRegex();

    private const int MaxEchoedNameLength = 64;

    /// <summary>Bounded, markup-free rendering of caller-supplied names for exception messages (no log/HTML/prompt injection).</summary>
    private static string Safe(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var length = Math.Min(value.Length, MaxEchoedNameLength);
        var chars = new char[length + (value.Length > length ? 3 : 0)];
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            chars[i] = char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ' ' ? c : '?';
        }

        if (value.Length > length)
        {
            chars[length] = chars[length + 1] = chars[length + 2] = '.';
        }

        return new string(chars);
    }

    /// <summary>Returns metadata with the definition in catalog spelling; throws <see cref="TableDefinitionException"/>.</summary>
    public static TableMetadata Validate(TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var primaryKey = new List<string>();
        if (metadata.PrimaryKeyColumns is { Count: > 0 } declaredPk)
        {
            if (declaredPk.Count > MaxColumnsPerKey)
            {
                throw new TableDefinitionException(
                    $"Primary key on table '{metadata.Identifier}' has {declaredPk.Count} columns, exceeding maximum {MaxColumnsPerKey}.");
            }

            var seenPk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var colName in declaredPk)
            {
                if (string.IsNullOrWhiteSpace(colName))
                {
                    throw new TableDefinitionException($"Primary key on table '{metadata.Identifier}' contains an empty column name.");
                }

                var col = metadata.GetColumn(colName.Trim())
                    ?? throw new TableDefinitionException(
                        $"Primary key column '{Safe(colName)}' does not exist in table '{metadata.Identifier}'.");

                if (!seenPk.Add(col.ColumnName))
                {
                    throw new TableDefinitionException(
                        $"Primary key on table '{metadata.Identifier}' lists column '{col.ColumnName}' more than once.");
                }

                primaryKey.Add(col.ColumnName);
            }
        }

        var indexes = new List<TableIndexDefinition>();
        if (metadata.Indexes is { Count: > 0 } declaredIndexes)
        {
            if (declaredIndexes.Count > MaxIndexesPerTable)
            {
                throw new TableDefinitionException(
                    $"Table '{metadata.Identifier}' declares {declaredIndexes.Count} indexes, exceeding maximum {MaxIndexesPerTable}.");
            }

            var seenIndexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var index in declaredIndexes)
            {
                if (index == null || string.IsNullOrWhiteSpace(index.Name) || !IndexNameRegex().IsMatch(index.Name))
                {
                    throw new TableDefinitionException(
                        $"Index name '{Safe(index?.Name)}' on table '{metadata.Identifier}' is invalid.");
                }

                if (!seenIndexNames.Add(index.Name))
                {
                    throw new TableDefinitionException(
                        $"Index name '{Safe(index.Name)}' is declared more than once on table '{metadata.Identifier}'.");
                }

                if (index.Columns == null || index.Columns.Count == 0 || index.Columns.Count > MaxColumnsPerKey)
                {
                    throw new TableDefinitionException(
                        $"Index '{Safe(index.Name)}' on table '{metadata.Identifier}' must have between 1 and {MaxColumnsPerKey} columns.");
                }

                var normalizedCols = new List<string>(index.Columns.Count);
                var seenCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var colName in index.Columns)
                {
                    if (string.IsNullOrWhiteSpace(colName))
                    {
                        throw new TableDefinitionException(
                            $"Index '{Safe(index.Name)}' on table '{metadata.Identifier}' contains an empty column name.");
                    }

                    var col = metadata.GetColumn(colName.Trim())
                        ?? throw new TableDefinitionException(
                            $"Index '{Safe(index.Name)}' references unknown column '{Safe(colName)}' on table '{metadata.Identifier}'.");

                    if (!seenCols.Add(col.ColumnName))
                    {
                        throw new TableDefinitionException(
                            $"Index '{Safe(index.Name)}' on table '{metadata.Identifier}' lists column '{col.ColumnName}' more than once.");
                    }

                    normalizedCols.Add(col.ColumnName);
                }

                indexes.Add(new TableIndexDefinition(index.Name, normalizedCols, index.IsUnique));
            }
        }

        return metadata with
        {
            PrimaryKeyColumns = primaryKey,
            Indexes = indexes
        };
    }
}

/// <summary>The declared key or index definition of a table is invalid (mapped to HTTP 400).</summary>
public sealed class TableDefinitionException(string message) : ArgumentException(message);
