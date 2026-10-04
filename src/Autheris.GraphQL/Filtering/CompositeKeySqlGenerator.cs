using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;

namespace Autheris.GraphQL.Filtering;

/// <summary>
/// Erzeugt dialektspezifische SQL-Prädikate und Joins für Composite Keys
/// (Tuple-IN für Postgres/SQLite/Databricks, Values-JOIN & OR-Ketten für SQL Server).
/// </summary>
public sealed partial class CompositeKeySqlGenerator
{
    public (string SqlClause, IReadOnlyDictionary<string, object?> Parameters) GenerateCompositeKeyPredicate(
        IReadOnlyList<string> columns,
        IReadOnlyList<CompositeKey> keys,
        DatabaseDialect dialect,
        int startParamCounter = 1)
    {
        if (columns == null || columns.Count == 0 || keys == null || keys.Count == 0)
        {
            return ("1 = 0", new Dictionary<string, object?>());
        }

        if (dialect == DatabaseDialect.SqlServer)
        {
            return GenerateDisjunctiveOrPredicate(columns, keys, dialect, startParamCounter);
        }

        // Tuple-IN für PostgreSQL, SQLite und Databricks:
        // ("colA", "colB") IN (($1, $2), ($3, $4))
        var parameters = new Dictionary<string, object?>();
        int counter = startParamCounter;

        var quotedCols = columns.Select(c => QuoteIdentifier(c, dialect)).ToList();
        var tupleHeader = $"({string.Join(", ", quotedCols)})";

        var tupleValues = new List<string>(keys.Count);

        foreach (var key in keys)
        {
            var paramPlaceholders = new List<string>(columns.Count);

            for (int i = 0; i < columns.Count; i++)
            {
                var val = i < key.Count ? key.Values[i] : null;
                var paramName = GetParamPlaceholder(dialect, counter++);
                parameters[paramName.TrimStart('@', '$', ':')] = val;
                paramPlaceholders.Add(paramName);
            }

            tupleValues.Add($"({string.Join(", ", paramPlaceholders)})");
        }

        var sql = $"{tupleHeader} IN ({string.Join(", ", tupleValues)})";
        return (sql, parameters);
    }

    public (string SqlJoinClause, IReadOnlyDictionary<string, object?> Parameters) GenerateValuesJoinClause(
        IReadOnlyList<string> columns,
        IReadOnlyList<CompositeKey> keys,
        DatabaseDialect dialect,
        string targetTableAlias = "c",
        int startParamCounter = 1)
    {
        if (columns == null || columns.Count == 0 || keys == null || keys.Count == 0)
        {
            return (string.Empty, new Dictionary<string, object?>());
        }

        ValidateIdentifier(targetTableAlias, nameof(targetTableAlias));

        var parameters = new Dictionary<string, object?>();
        int counter = startParamCounter;

        var quotedCols = columns.Select(c => QuoteIdentifier(c, dialect)).ToList();
        var rowValues = new List<string>(keys.Count);

        foreach (var key in keys)
        {
            var paramPlaceholders = new List<string>(columns.Count);
            for (int i = 0; i < columns.Count; i++)
            {
                var val = i < key.Count ? key.Values[i] : null;
                var paramName = GetParamPlaceholder(dialect, counter++);
                parameters[paramName.TrimStart('@', '$', ':')] = val;
                paramPlaceholders.Add(paramName);
            }
            rowValues.Add($"({string.Join(", ", paramPlaceholders)})");
        }

        var onClauses = quotedCols.Select(qc => $"{targetTableAlias}.{qc} = _k.{qc}").ToList();

        var sql = $"INNER JOIN (VALUES {string.Join(", ", rowValues)}) AS _k({string.Join(", ", quotedCols)}) ON {string.Join(" AND ", onClauses)}";
        return (sql, parameters);
    }

    public (string SqlClause, IReadOnlyDictionary<string, object?> Parameters) GenerateDisjunctiveOrPredicate(
        IReadOnlyList<string> columns,
        IReadOnlyList<CompositeKey> keys,
        DatabaseDialect dialect,
        int startParamCounter = 1)
    {
        if (columns == null || columns.Count == 0 || keys == null || keys.Count == 0)
        {
            return ("1 = 0", new Dictionary<string, object?>());
        }

        var parameters = new Dictionary<string, object?>();
        int counter = startParamCounter;

        var quotedCols = columns.Select(c => QuoteIdentifier(c, dialect)).ToList();
        var andBranches = new List<string>(keys.Count);

        foreach (var key in keys)
        {
            var comparisons = new List<string>(columns.Count);

            for (int i = 0; i < columns.Count; i++)
            {
                var val = i < key.Count ? key.Values[i] : null;
                var paramName = GetParamPlaceholder(dialect, counter++);
                parameters[paramName.TrimStart('@', '$', ':')] = val;
                comparisons.Add($"{quotedCols[i]} = {paramName}");
            }

            andBranches.Add($"({string.Join(" AND ", comparisons)})");
        }

        var sql = andBranches.Count == 1
            ? andBranches[0]
            : $"({string.Join(" OR ", andBranches)})";

        return (sql, parameters);
    }

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    public static void ValidateIdentifier(string id, string paramName)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdentifierRegex().IsMatch(id))
        {
            throw new ArgumentException($"Ungültiger Bezeichner '{id}'. Erlaubt sind nur alphanumerische Zeichen und Unterstriche, beginnend mit einem Buchstaben oder Unterstrich.", paramName);
        }
    }

    private static string QuoteIdentifier(string identifier, DatabaseDialect dialect) =>
        dialect.QuoteIdentifier(identifier);

    private static string GetParamPlaceholder(DatabaseDialect dialect, int num) =>
        dialect switch
        {
            DatabaseDialect.PostgreSql => $"${num}",
            DatabaseDialect.Oracle => $":p{num}",
            _ => $"@p{num}"
        };
}
