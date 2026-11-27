using Autheris.Application.Analytics;
using Autheris.GraphQL.Catalog;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.FlatAggregateTestSupport;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>The row field classifier of the flat <c>_groups</c> type: which fields exist per column type and how names stay unique.</summary>
public sealed class FlatAggregateRowFieldsTests
{
    private static IReadOnlyList<string> AggregateNames(IReadOnlyList<FlatAggregateRowField> fields, string column) =>
        fields.Where(f => f.Role == FlatAggregateRowFieldRole.Aggregate && f.Column?.FieldName == column).Select(f => f.FieldName).ToList();

    [Fact]
    public void RowFields_FollowKindForMatrix_PerColumnType()
    {
        var table = Table(
            ("i", CatalogFieldType.Int),
            ("s", CatalogFieldType.String),
            ("b", CatalogFieldType.Boolean),
            ("d", CatalogFieldType.DateTime),
            ("m", CatalogFieldType.Decimal),
            ("f", CatalogFieldType.Float));

        var fields = FlatAggregateRowFields.For(table);

        AggregateNames(fields, "i").ShouldBe(["i_count_distinct", "i_sum", "i_avg", "i_min", "i_max"]);
        AggregateNames(fields, "s").ShouldBe(["s_count_distinct", "s_min", "s_max"]);
        AggregateNames(fields, "b").ShouldBe(["b_count_distinct"]);
        AggregateNames(fields, "d").ShouldBe(["d_count_distinct", "d_min", "d_max"]);
        AggregateNames(fields, "m").ShouldBe(["m_count_distinct", "m_sum", "m_avg", "m_min", "m_max"]);
        AggregateNames(fields, "f").ShouldBe(["f_count_distinct", "f_sum", "f_avg", "f_min", "f_max"]);
        foreach (var field in fields.Where(f => f.Role == FlatAggregateRowFieldRole.Aggregate))
        {
            field.Kind.ShouldBe(AggregateResultMapper.KindFor(field.Function!.Value, field.Column!.FieldType));
        }

        FlatAggregateRowFields.Find(table, "count")!.Role.ShouldBe(FlatAggregateRowFieldRole.Count);
        FlatAggregateRowFields.Find(table, AggregateTypeNames.MergedOtherField)!.Role.ShouldBe(FlatAggregateRowFieldRole.MergedOther);
        FlatAggregateRowFields.Find(table, "i_sum")!.Function.ShouldBe(AggregateFunction.Sum);
        FlatAggregateRowFields.Find(table, "nope").ShouldBeNull();
        FlatAggregateRowFields.RootFieldName(table).ShouldBe("sales_dbo_orders_groups");
    }

    [Fact]
    public void ColumnNamedCount_KeepsName_GeneratedCountGetsSuffix()
    {
        var table = Table(("count", CatalogFieldType.Int));

        FlatAggregateRowFields.Find(table, "count")!.Role.ShouldBe(FlatAggregateRowFieldRole.Column);
        FlatAggregateRowFields.Find(table, "count_2")!.Role.ShouldBe(FlatAggregateRowFieldRole.Count);
    }

    [Fact]
    public void GeneratedNameCollidingWithColumn_GetsNumericSuffix_Deterministic()
    {
        (string, CatalogFieldType)[] columns = [("a", CatalogFieldType.Int), ("a_sum", CatalogFieldType.Int), ("a_sum_2", CatalogFieldType.Int)];
        var first = Table(columns);
        var second = Table(columns);

        FlatAggregateRowFields.Find(first, "a_sum")!.Role.ShouldBe(FlatAggregateRowFieldRole.Column);
        var sumOfA = FlatAggregateRowFields.For(first)
            .Single(f => f.Role == FlatAggregateRowFieldRole.Aggregate && f.Function == AggregateFunction.Sum && f.Column!.FieldName == "a");
        sumOfA.FieldName.ShouldBe("a_sum_3");
        FlatAggregateRowFields.For(first).Select(f => f.FieldName).ShouldBe(FlatAggregateRowFields.For(second).Select(f => f.FieldName));
        FlatAggregateRowFields.For(first).Select(f => f.FieldName).ShouldBeUnique();
    }

    [Fact]
    public void ColumnNamedIsMergedOther_Deterministic()
    {
        var table = Table((AggregateTypeNames.MergedOtherField, CatalogFieldType.String));

        FlatAggregateRowFields.Find(table, AggregateTypeNames.MergedOtherField)!.Role.ShouldBe(FlatAggregateRowFieldRole.Column);
        FlatAggregateRowFields.Find(table, AggregateTypeNames.MergedOtherField + "_2")!.Role.ShouldBe(FlatAggregateRowFieldRole.MergedOther);
        FlatAggregateRowFields.For(table).Select(f => f.FieldName).ShouldBeUnique();
    }
}
