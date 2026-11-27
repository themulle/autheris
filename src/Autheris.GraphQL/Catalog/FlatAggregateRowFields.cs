using System.Runtime.CompilerServices;
using Autheris.Application.Analytics;

namespace Autheris.GraphQL.Catalog;

/// <summary>What a field of the flat <c>&lt;table&gt;_group_row</c> type stands for.</summary>
public enum FlatAggregateRowFieldRole
{
    /// <summary>A group column; selecting it adds the column to the group set.</summary>
    Column,

    /// <summary><c>COUNT(*)</c> of the group.</summary>
    Count,

    /// <summary>Marker of the merged OTHER row; never sent to the engine.</summary>
    MergedOther,

    /// <summary>One aggregate function over one column.</summary>
    Aggregate
}

/// <summary>One field of the flat group row type with its meaning.</summary>
public sealed record FlatAggregateRowField(
    string FieldName,
    FlatAggregateRowFieldRole Role,
    CatalogColumnField? Column,
    AggregateFunction? Function,
    AggregateValueKind? Kind);

/// <summary>
/// The single classifier of the flat group row type: which fields a table's row has, what each one means and its name. Names are
/// deterministic: column fields keep the entity field name, generated fields get <c>_2</c>, <c>_3</c> ... when a name is taken.
/// </summary>
public static class FlatAggregateRowFields
{
    private static readonly (AggregateFunction Function, string Suffix)[] Functions =
    [
        (AggregateFunction.CountDistinct, "count_distinct"),
        (AggregateFunction.Sum, "sum"),
        (AggregateFunction.Avg, "avg"),
        (AggregateFunction.Min, "min"),
        (AggregateFunction.Max, "max")
    ];

    private sealed record Cache(IReadOnlyList<FlatAggregateRowField> Fields, IReadOnlyDictionary<string, FlatAggregateRowField> ByName);

    private static readonly ConditionalWeakTable<CatalogTableType, Cache> Cached = new();

    /// <summary>The name of the flat root field of a table.</summary>
    public static string RootFieldName(CatalogTableType table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return AggregateTypeNames.For(table.TypeName).GroupsField;
    }

    public static IReadOnlyList<FlatAggregateRowField> For(CatalogTableType table) => Get(table).Fields;

    public static FlatAggregateRowField? Find(CatalogTableType table, string fieldName) =>
        Get(table).ByName.GetValueOrDefault(fieldName);

    private static Cache Get(CatalogTableType table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Cached.GetValue(table, Build);
    }

    private static Cache Build(CatalogTableType table)
    {
        var taken = new HashSet<string>(table.Columns.Select(c => c.FieldName), StringComparer.Ordinal);
        var fields = new List<FlatAggregateRowField>(table.Columns.Count * 6 + 2);
        foreach (var column in table.Columns)
        {
            fields.Add(new FlatAggregateRowField(column.FieldName, FlatAggregateRowFieldRole.Column, column, null, null));
        }

        fields.Add(new FlatAggregateRowField(
            Unique(AggregateTypeNames.CountField, taken), FlatAggregateRowFieldRole.Count, null, AggregateFunction.Count, AggregateValueKind.Int));
        fields.Add(new FlatAggregateRowField(
            Unique(AggregateTypeNames.MergedOtherField, taken), FlatAggregateRowFieldRole.MergedOther, null, null, null));

        foreach (var column in table.Columns)
        {
            foreach (var (function, suffix) in Functions)
            {
                if (AggregateResultMapper.KindFor(function, column.FieldType) is not { } kind)
                {
                    continue;
                }

                fields.Add(new FlatAggregateRowField(
                    Unique(column.FieldName + "_" + suffix, taken), FlatAggregateRowFieldRole.Aggregate, column, function, kind));
            }
        }

        return new Cache(fields, fields.ToDictionary(f => f.FieldName, StringComparer.Ordinal));
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++)
        {
            candidate = name + "_" + i;
        }

        return candidate;
    }

    /// <summary>The GraphQL scalar of an aggregate value kind.</summary>
    internal static string ScalarName(AggregateValueKind kind) => kind switch
    {
        AggregateValueKind.Int => "Long",
        AggregateValueKind.Decimal => "Decimal",
        AggregateValueKind.Float => "Float",
        AggregateValueKind.Boolean => "Boolean",
        AggregateValueKind.DateTime => "DateTime",
        _ => "String"
    };
}
