using System.Text;

namespace Autheris.Infrastructure.Persistence.Migrations.Shape;

/// <summary>Compares an expected shape with an actual one per plan D-3 (extra tables, columns and indexes are tolerated).</summary>
internal static class GovernanceSchemaShapeComparer
{
    internal const int MaxMessageLines = 50;

    public static IReadOnlyList<SchemaDeviation> Compare(GovernanceSchemaShape expected, GovernanceSchemaShape actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var actualTables = new Dictionary<string, SchemaTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in actual.Tables)
        {
            actualTables.TryAdd(t.Name, t);
        }

        var result = new List<SchemaDeviation>();
        foreach (var table in expected.Tables)
        {
            if (!actualTables.TryGetValue(table.Name, out var found))
            {
                result.Add(new SchemaDeviation(SchemaDeviationKind.MissingTable, table.Name, null, string.Empty, null));
                continue;
            }

            CompareColumns(table, found, result);
            if (!table.PrimaryKey.SequenceEqual(found.PrimaryKey, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new SchemaDeviation(
                    SchemaDeviationKind.WrongPrimaryKey, table.Name, null, Join(table.PrimaryKey), found.PrimaryKey.Count == 0 ? "none" : Join(found.PrimaryKey)));
            }

            CompareIndexes(table, found, result);
        }

        return result
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.Table, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Deterministic, bounded drift message (plan D-5). Contains identifiers only.</summary>
    public static string FormatMessage(
        string engine, string databaseLabel, IReadOnlyCollection<int> applied, IReadOnlyCollection<int> pending, IReadOnlyList<SchemaDeviation> deviations)
    {
        var sb = new StringBuilder();
        sb.Append("Governance schema drift on ").Append(engine).Append(" database '").Append(databaseLabel)
            .Append("': the schema does not match this build (applied migrations: ").Append(Versions(applied))
            .Append("; pending: ").Append(Versions(pending)).Append(").\n");
        foreach (var d in deviations.Take(MaxMessageLines))
        {
            sb.Append("  ").Append(KindLabel(d.Kind).PadRight(18)).Append(' ').Append(d.Table);
            if (d.Name is not null)
            {
                sb.Append('.').Append(d.Name);
            }

            if (d.Expected.Length > 0)
            {
                sb.Append(" (expected ").Append(d.Expected);
                if (d.Found is not null)
                {
                    sb.Append("; found ").Append(d.Found);
                }

                sb.Append(')');
            }

            sb.Append('\n');
        }

        if (deviations.Count > MaxMessageLines)
        {
            sb.Append("  ... and ").Append(deviations.Count - MaxMessageLines).Append(" more\n");
        }

        sb.Append("Autheris upgrades governance schemas only forward from the v1.5.0 baseline. Restore the matching backup or load the data into a current schema; ")
            .Append("see docs/features/f-gov-12-multi-engine-governance-storage.md#schema-shape-verification.");
        return sb.ToString();
    }

    private static void CompareColumns(SchemaTable expected, SchemaTable actual, List<SchemaDeviation> result)
    {
        var columns = new Dictionary<string, SchemaColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in actual.Columns)
        {
            columns.TryAdd(c.Name, c);
        }

        foreach (var column in expected.Columns)
        {
            if (!columns.TryGetValue(column.Name, out var found))
            {
                result.Add(new SchemaDeviation(SchemaDeviationKind.MissingColumn, expected.Name, column.Name, $"{column.Type}, family {column.Family}", null));
            }
            else if (!string.Equals(column.Family, found.Family, StringComparison.Ordinal))
            {
                result.Add(new SchemaDeviation(SchemaDeviationKind.WrongType, expected.Name, column.Name, $"family {column.Family}", found.Family));
            }
            else if (column.Nullable != found.Nullable)
            {
                result.Add(new SchemaDeviation(
                    SchemaDeviationKind.WrongNullability, expected.Name, column.Name, column.Nullable ? "NULL" : "NOT NULL", found.Nullable ? "NULL" : "NOT NULL"));
            }
        }
    }

    private static void CompareIndexes(SchemaTable expected, SchemaTable actual, List<SchemaDeviation> result)
    {
        var indexes = new Dictionary<string, SchemaIndex>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in actual.Indexes)
        {
            indexes.TryAdd(i.Name, i);
        }

        foreach (var index in expected.Indexes)
        {
            if (!indexes.TryGetValue(index.Name, out var found))
            {
                result.Add(new SchemaDeviation(SchemaDeviationKind.MissingIndex, expected.Name, index.Name, Describe(index), null));
            }
            else if (index.Unique != found.Unique || !index.Columns.SequenceEqual(found.Columns, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new SchemaDeviation(SchemaDeviationKind.WrongIndex, expected.Name, index.Name, Describe(index), Describe(found)));
            }
        }
    }

    private static string Describe(SchemaIndex index) => (index.Unique ? "UNIQUE (" : "(") + Join(index.Columns) + ")";

    private static string Join(IReadOnlyList<string> values) => string.Join(", ", values);

    private static string Versions(IReadOnlyCollection<int> versions) =>
        versions.Count == 0 ? "none" : string.Join(", ", versions.Order().Select(v => v.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)));

    private static string KindLabel(SchemaDeviationKind kind) => kind switch
    {
        SchemaDeviationKind.MissingTable => "missing table",
        SchemaDeviationKind.MissingColumn => "missing column",
        SchemaDeviationKind.WrongType => "wrong type",
        SchemaDeviationKind.WrongPrimaryKey => "wrong primary key",
        SchemaDeviationKind.MissingIndex => "missing index",
        SchemaDeviationKind.WrongIndex => "wrong index",
        SchemaDeviationKind.WrongNullability => "wrong nullability",
        _ => "deviation"
    };
}
