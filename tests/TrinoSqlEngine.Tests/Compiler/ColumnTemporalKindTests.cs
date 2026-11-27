using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

public class ColumnTemporalKindTests
{
    public static IEnumerable<object[]> Dialects() => ClientListHarness.DialectData();

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Zoned_type_names_are_with_zone(TargetSqlDialect dialect)
    {
        foreach (string type in new[] { "timestamptz", "TIMESTAMP WITH TIME ZONE", "timestamp(6) with time zone", "timestamp with local time zone", "datetimeoffset(7)", "timestamp_ltz", "  timestamptz  " })
        {
            Assert.Equal(ColumnTemporalKind.WithZone, ColumnBindAdapter.TemporalKind(dialect, type));
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Zoneless_type_names_are_without_zone(TargetSqlDialect dialect)
    {
        foreach (string type in new[] { "timestamp without time zone", "datetime", "datetime2(3)", "smalldatetime", "date", "timestamp_ntz" })
        {
            Assert.Equal(ColumnTemporalKind.WithoutZone, ColumnBindAdapter.TemporalKind(dialect, type));
        }

        Assert.Equal(dialect == TargetSqlDialect.Databricks ? ColumnTemporalKind.WithZone : ColumnTemporalKind.WithoutZone, ColumnBindAdapter.TemporalKind(dialect, "timestamp"));
        Assert.Equal(dialect == TargetSqlDialect.Databricks ? ColumnTemporalKind.WithZone : ColumnTemporalKind.WithoutZone, ColumnBindAdapter.TemporalKind(dialect, "timestamp(6)"));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Other_or_missing_types_are_unknown(TargetSqlDialect dialect)
    {
        foreach (string? type in new[] { null, "", "  ", "varchar(10)", "int", "timestamp(", "time", "timestamp with" })
        {
            Assert.Equal(ColumnTemporalKind.Unknown, ColumnBindAdapter.TemporalKind(dialect, type));
        }
    }
}
