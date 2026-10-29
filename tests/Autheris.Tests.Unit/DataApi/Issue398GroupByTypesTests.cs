namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>#398: groupBy on (max) text keys. Binding conditions I398-SC-01..07 / I398-T-01..09.</summary>
[Trait("Category", "Security")]
public sealed class Issue398GroupByTypesTests
{
    private static readonly TableIdentifier Notes = new("sales", "dbo", "notes");

    private static TableMetadata Meta(string sourceType, params (string Name, string Type)[] cols) => new()
    {
        Identifier = Notes,
        Table = new Table
        {
            Id = Guid.NewGuid(), SourceType = sourceType, SourceName = "sales", SchemaName = "dbo", TableName = "notes",
            DataSourceType = DataSourceType.Sql, IsActive = true
        },
        Columns = cols.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList(),
        PrimaryKeyColumns = ["id"]
    };

    private static TableAccessDecision Decision(params (string Name, ColumnAccessLevel Level)[] levels) =>
        TableAccessDecision.Allowed(
            Notes,
            levels.ToDictionary(l => l.Name, l => l.Level, StringComparer.OrdinalIgnoreCase),
            hasUnconstrainedColumnAllow: true);

    private static TableAggregateIntent GroupBy(string column, TableFilterClause? filter = null) =>
        new(Notes, [column], [new AggregateSelection(AggregateFunction.Count)], filter, null, 10);

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "nvarchar(max)", true)]
    [InlineData(DatabaseDialect.SqlServer, "NVARCHAR(MAX)", true)]
    [InlineData(DatabaseDialect.SqlServer, "varchar(max)", true)]
    [InlineData(DatabaseDialect.SqlServer, "nvarchar(200)", true)]
    [InlineData(DatabaseDialect.SqlServer, "int", true)]
    [InlineData(DatabaseDialect.SqlServer, "datetime2", true)]
    [InlineData(DatabaseDialect.SqlServer, "uniqueidentifier", true)]
    [InlineData(DatabaseDialect.SqlServer, "varbinary(max)", false)]
    [InlineData(DatabaseDialect.SqlServer, "varbinary(50)", false)]
    [InlineData(DatabaseDialect.SqlServer, "xml", false)]
    [InlineData(DatabaseDialect.SqlServer, "text", false)]
    [InlineData(DatabaseDialect.SqlServer, "ntext", false)]
    [InlineData(DatabaseDialect.SqlServer, "image", false)]
    [InlineData(DatabaseDialect.SqlServer, "geography", false)]
    [InlineData(DatabaseDialect.SqlServer, "geometry", false)]
    [InlineData(DatabaseDialect.SqlServer, "sql_variant", false)]
    [InlineData(DatabaseDialect.SqlServer, "hierarchyid", false)]
    [InlineData(DatabaseDialect.SqlServer, "some_clr_type", false)]
    [InlineData(DatabaseDialect.SqlServer, "", false)]
    [InlineData(DatabaseDialect.SqlServer, null, false)]
    [InlineData(DatabaseDialect.PostgreSql, "text", true)]
    [InlineData(DatabaseDialect.PostgreSql, "character varying(20)", true)]
    [InlineData(DatabaseDialect.PostgreSql, "integer", true)]
    [InlineData(DatabaseDialect.PostgreSql, "timestamp with time zone", true)]
    [InlineData(DatabaseDialect.PostgreSql, "nvarchar(max)", false)]
    [InlineData(DatabaseDialect.PostgreSql, "json", false)]
    [InlineData(DatabaseDialect.PostgreSql, "jsonb", false)]
    [InlineData(DatabaseDialect.PostgreSql, "bytea", false)]
    [InlineData(DatabaseDialect.PostgreSql, "integer[]", false)]
    [InlineData(DatabaseDialect.PostgreSql, "mystery", false)]
    [InlineData(DatabaseDialect.Oracle, "varchar2(50)", true)]
    [InlineData(DatabaseDialect.Oracle, "number(10,2)", true)]
    [InlineData(DatabaseDialect.Oracle, "clob", false)]
    [InlineData(DatabaseDialect.Oracle, "nclob", false)]
    [InlineData(DatabaseDialect.Oracle, "blob", false)]
    [InlineData(DatabaseDialect.Oracle, "long", false)]
    [InlineData(DatabaseDialect.Oracle, "text", false)]
    [InlineData(DatabaseDialect.Oracle, "mystery", false)]
    [InlineData(DatabaseDialect.Databricks, "string", true)]
    [InlineData(DatabaseDialect.Databricks, "int", true)]
    [InlineData(DatabaseDialect.Databricks, "ARRAY<INT>", false)]
    [InlineData(DatabaseDialect.Databricks, "MAP<STRING,INT>", false)]
    [InlineData(DatabaseDialect.Databricks, "binary", false)]
    [InlineData(DatabaseDialect.Databricks, "mystery", false)]
    [InlineData(DatabaseDialect.Sqlite, "TEXT", true)]
    [InlineData(DatabaseDialect.Sqlite, "BLOB", true)]
    [InlineData(DatabaseDialect.Sqlite, "mystery", true)]
    [InlineData(DatabaseDialect.Sqlite, "", true)]
    public void I398_T_01_IsGroupableType_Matrix_PerDialect_AllowsOnlyDocumentedTypes(DatabaseDialect dialect, string? type, bool expected) =>
        DefaultReadOrdering.IsGroupableType(type, dialect).ShouldBe(expected);

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "nvarchar(max)", false)]
    [InlineData(DatabaseDialect.SqlServer, "varchar(max)", false)]
    [InlineData(DatabaseDialect.SqlServer, "varbinary(max)", false)]
    [InlineData(DatabaseDialect.SqlServer, "nvarchar(200)", true)]
    [InlineData(DatabaseDialect.SqlServer, "some_unknown_type", true)]
    public void I398_T_02_IsOrderableType_Unchanged_MaxStillNotOrderable(DatabaseDialect dialect, string type, bool expected) =>
        DefaultReadOrdering.IsOrderableType(type, dialect).ShouldBe(expected);

    [Fact]
    public void MaxTextKey_OnSqlServer_PlansGroupByColumnOnly_NoCast()
    {
        // I398-T-08 / SC-05: GROUP BY "col", no CAST/LEFT/SUBSTRING of the key.
        var meta = Meta("sqlserver", ("id", "int"), ("note", "nvarchar(max)"));
        var planned = new GovernedTableReadPlanner().PlanAggregate(
            GroupBy("note"), meta, Decision(("id", ColumnAccessLevel.Clear), ("note", ColumnAccessLevel.Clear)));

        planned.Sql.ShouldContain("GROUP BY \"note\"");
        planned.Sql.ToUpperInvariant().ShouldNotContain("CAST");
        planned.Sql.ToUpperInvariant().ShouldNotContain("SUBSTRING");
        planned.Sql.ToUpperInvariant().ShouldNotContain("LEFT(");
    }

    [Fact]
    public void MaxTextKey_RowFilter_StaysInWhereBeforeGroupBy()
    {
        // I398-T-07: the filter (and thus row-level policy composed with it) precedes GROUP BY.
        var meta = Meta("sqlserver", ("id", "int"), ("note", "nvarchar(max)"), ("region", "varchar(10)"));
        var filter = TableFilterClause.FromTrinoText("\"region\" = 'eu'", ["region"]);
        var planned = new GovernedTableReadPlanner().PlanAggregate(
            GroupBy("note", filter), meta,
            Decision(("id", ColumnAccessLevel.Clear), ("note", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear)));

        var where = planned.Sql.IndexOf(" WHERE ", StringComparison.Ordinal);
        where.ShouldBeGreaterThan(0);
        where.ShouldBeLessThan(planned.Sql.IndexOf(" GROUP BY ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sqlserver", "varbinary(max)")]
    [InlineData("sqlserver", "xml")]
    [InlineData("sqlserver", "text")]
    [InlineData("sqlserver", "geography")]
    [InlineData("sqlserver", "clr_unknown")]
    [InlineData("postgres", "jsonb")]
    [InlineData("postgres", "mystery")]
    [InlineData("oracle", "clob")]
    public void I398_T_03_RefusedTypes_AllMapToNotSupported_IdenticalFixedMessage_NoTypeOrColumnName(string source, string type)
    {
        var meta = Meta(source, ("id", "int"), ("zz_col", type));
        var ex = Should.Throw<SqlCompileNotSupportedException>(() => new GovernedTableReadPlanner().PlanAggregate(
            GroupBy("zz_col"), meta, Decision(("id", ColumnAccessLevel.Clear), ("zz_col", ColumnAccessLevel.Clear))));

        ex.Reason.ShouldBe(SqlCompileNotSupportedReason.Construct);
        ex.Message.ShouldNotContain("zz_col");
        ex.Message.ShouldNotContain(type);

        var other = Should.Throw<SqlCompileNotSupportedException>(() => new GovernedTableReadPlanner().PlanAggregate(
            GroupBy("other_col"),
            Meta("sqlserver", ("id", "int"), ("other_col", "image")),
            Decision(("id", ColumnAccessLevel.Clear), ("other_col", ColumnAccessLevel.Clear))));
        other.Message.ShouldBe(ex.Message);
    }

    [Fact]
    public void I398_T_04_MaskedMaxColumn_GroupBy_RefusedAsMasked_BeforeTypeCheck()
    {
        var meta = Meta("sqlserver", ("id", "int"), ("note", "varbinary(max)"));
        Should.Throw<Autheris.Application.Analytics.MaskedAggregationException>(() => new GovernedTableReadPlanner().PlanAggregate(
            GroupBy("note"), meta, Decision(("id", ColumnAccessLevel.Clear), ("note", ColumnAccessLevel.Mask))));
    }

    [Fact]
    public void I398_T_04b_UnavailableColumn_NoTypeOracle()
    {
        var meta = Meta("sqlserver", ("id", "int"), ("hidden", "varbinary(max)"), ("fine", "varchar(10)"));
        var decision = Decision(("id", ColumnAccessLevel.Clear), ("hidden", ColumnAccessLevel.Deny), ("fine", ColumnAccessLevel.Clear));
        var planner = new GovernedTableReadPlanner();

        var denied = Should.Throw<ArgumentException>(() => planner.PlanAggregate(GroupBy("hidden"), meta, decision));
        var unknown = Should.Throw<ArgumentException>(() => planner.PlanAggregate(GroupBy("nope"), meta, decision));

        denied.GetType().ShouldBe(unknown.GetType());
        denied.Message.Replace("'hidden'", "'X'").ShouldBe(unknown.Message.Replace("'nope'", "'X'"));
    }

    /// <summary>A reader whose column 0 is a string (or an endless string when <paramref name="value"/> is null); counts the chars handed out.</summary>
    private static DbDataReader StringReader(string? value, Action<int> onChars)
    {
        var reader = Substitute.For<DbDataReader>();
        reader.GetFieldType(Arg.Any<int>()).Returns(typeof(string));
        reader.IsDBNull(Arg.Any<int>()).Returns(false);
        reader.GetChars(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<char[]>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var buffer = call.ArgAt<char[]>(2);
                var dataOffset = call.ArgAt<long>(1);
                var bufferOffset = call.ArgAt<int>(3);
                var length = call.ArgAt<int>(4);
                int n = value is null ? length : (int)Math.Clamp(value.Length - dataOffset, 0, length);
                for (int i = 0; i < n; i++)
                {
                    buffer[bufferOffset + i] = value is null ? 'a' : value[(int)dataOffset + i];
                }

                onChars(n);
                return (long)n;
            });
        return reader;
    }

    [Fact]
    public void I398_T_06_OversizedKey_ExceedsPerKeyBudget_Throws_WithoutReadingTheWholeValue()
    {
        long requested = 0;
        var reader = StringReader(null, n => requested += n); // an endless 2 GB value

        var budget = new ResultByteBudget(MaxValueBytes: 1024, MaxTotalBytes: 1_000_000);
        var total = 0L;
        var ex = Should.Throw<AggregationRowCapExceededException>(() => budget.ReadValue(reader, 0, ref total));

        ex.Message.ShouldBe(AggregationRowCapExceededException.FixedMessage);
        requested.ShouldBeLessThan(1024 + 1);
    }

    [Fact]
    public void ResultByteBudget_TotalBudgetExceeded_Throws_AndSmallValuesPass()
    {
        var value = new string('x', 300);
        var reader = StringReader(value, _ => { });

        var budget = new ResultByteBudget(MaxValueBytes: 1024, MaxTotalBytes: 1000);
        var total = 0L;
        budget.ReadValue(reader, 0, ref total).ShouldBe(value);
        total.ShouldBe(600); // UTF-16 bytes
        Should.Throw<AggregationRowCapExceededException>(() => budget.ReadValue(reader, 0, ref total));
    }

    [Fact]
    public void ResultByteBudget_NonStringAndNull_PassThrough()
    {
        var table = new DataTable();
        table.Columns.Add("n", typeof(long));
        table.Columns.Add("s", typeof(string));
        table.Rows.Add(7L, DBNull.Value);
        using var reader = table.CreateDataReader();
        reader.Read().ShouldBeTrue();

        var budget = ResultByteBudget.GroupKeys;
        var total = 0L;
        budget.ReadValue(reader, 0, ref total).ShouldBe(7L);
        budget.ReadValue(reader, 1, ref total).ShouldBeNull();
        total.ShouldBe(0);
    }

    [Fact]
    public void I398_T_09_BudgetFailure_CarriesNoKeyValues()
    {
        var reader = StringReader("alice@example.com" + new string('y', 2000), _ => { });

        var total = 0L;
        var ex = Should.Throw<AggregationRowCapExceededException>(
            () => new ResultByteBudget(100, 1000).ReadValue(reader, 0, ref total));
        ex.ToString().ShouldNotContain("alice");
    }
}
