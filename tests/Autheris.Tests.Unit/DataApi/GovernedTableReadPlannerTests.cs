namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Extensions.OData;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>WP-ARC-B-01: the shared governed table-read planner.</summary>
[Trait("Category", "Security")]
public sealed class GovernedTableReadPlannerTests
{
    private static readonly TableIdentifier Customers = new("sales", "dbo", "customers");

    private readonly GovernedTableReadPlanner _planner = new();

    private static TableMetadata Metadata(IReadOnlyList<string>? primaryKey = null) => new()
    {
        Identifier = Customers,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "customers",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = new List<TableColumn>
        {
            new() { ColumnName = "id", DataType = "int" },
            new() { ColumnName = "email", DataType = "varchar", IsSensitive = true },
            new() { ColumnName = "name", DataType = "varchar" },
            new() { ColumnName = "secret", DataType = "varchar" }
        },
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = new() { RuleType = "MASK_EMAIL" }
        },
        PrimaryKeyColumns = primaryKey ?? new[] { "id" }
    };

    private static TableAccessDecision Decision() => TableAccessDecision.Allowed(
        Customers,
        new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask,
            ["name"] = ColumnAccessLevel.Clear,
            ["secret"] = ColumnAccessLevel.Deny
        },
        hasUnconstrainedColumnAllow: true);

    private static TableReadIntent Intent(
        IReadOnlyList<string>? select = null,
        TableFilterClause? filter = null,
        IReadOnlyList<TableOrderBy>? orderBy = null,
        int limit = 51,
        int offset = 0) =>
        new(Customers, select, filter, orderBy, limit, offset, "Rest");

    [Fact]
    public void Plan_FilterOnMaskedColumn_Throws()
    {
        var filter = TableFilterClause.FromTrinoText("\"email\" = 'a@b.c'", new[] { "email" });

        var ex = Should.Throw<ArgumentException>(() => _planner.Plan(Intent(filter: filter), Metadata(), Decision()));
        ex.Message.ShouldContain("email");
        ex.Message.ShouldContain("oracle inference");
    }

    [Fact]
    public void Plan_FilterOnDeniedColumn_Throws()
    {
        var filter = TableFilterClause.FromTrinoText("\"secret\" = 'x'", new[] { "secret" });

        Should.Throw<ArgumentException>(() => _planner.Plan(Intent(filter: filter), Metadata(), Decision()));
    }

    [Fact]
    public void Plan_FilterOnUnknownColumn_Throws()
    {
        var filter = TableFilterClause.FromTrinoText("\"nope\" = 'x'", new[] { "nope" });

        Should.Throw<ArgumentException>(() => _planner.Plan(Intent(filter: filter), Metadata(), Decision()))
            .Message.ShouldContain("nope");
    }

    [Fact]
    public void Plan_OrderByMaskedColumn_Throws()
    {
        var order = new[] { new TableOrderBy("email") };

        Should.Throw<ArgumentException>(() => _planner.Plan(Intent(orderBy: order), Metadata(), Decision()))
            .Message.ShouldContain("Ordering on masked or denied column");
    }

    [Fact]
    public void Plan_DeniedSelectColumn_Throws()
    {
        Should.Throw<ArgumentException>(() => _planner.Plan(Intent(select: new[] { "id", "secret" }), Metadata(), Decision()));
    }

    [Fact]
    public void Plan_UnknownSelectColumn_Throws()
    {
        Should.Throw<ArgumentException>(() => _planner.Plan(Intent(select: new[] { "nope" }), Metadata(), Decision()));
    }

    [Fact]
    public void Plan_NoSelect_SelectsEveryNonDeniedColumn()
    {
        var planned = _planner.Plan(Intent(), Metadata(), Decision());

        planned.ColumnsToSelect.ShouldBe(new[] { "id", "email", "name" });
        planned.Sql.ShouldStartWith("SELECT \"id\", \"email\", \"name\" FROM sales.dbo.customers");
        planned.Sql.ShouldNotContain("secret");
    }

    [Fact]
    public void Plan_DeniedDecision_ThrowsBeforePlanning()
    {
        var denied = TableAccessDecision.Denied(Customers, "no consent");

        Should.Throw<UnauthorizedAccessException>(() => _planner.Plan(Intent(), Metadata(), denied))
            .Message.ShouldContain("no consent");
    }

    [Fact]
    public void Plan_NoOrder_FallsBackToPrimaryKey()
    {
        var planned = _planner.Plan(Intent(select: new[] { "id" }), Metadata(new[] { "id", "name" }), Decision());

        planned.Sql.ShouldBe("SELECT \"id\" FROM sales.dbo.customers ORDER BY \"id\", \"name\" LIMIT 51");
    }

    [Fact]
    public void Plan_WithoutOrderAndWithoutPrimaryKey_EmitsNoOrderBy()
    {
        var planned = _planner.Plan(Intent(select: new[] { "id" }), Metadata(Array.Empty<string>()), Decision());

        planned.Sql.ShouldNotContain("ORDER BY");
    }

    [Fact]
    public void Plan_OffsetAndOrder_EmitsTrinoPagination()
    {
        var planned = _planner.Plan(
            Intent(select: new[] { "id", "name" }, orderBy: new[] { new TableOrderBy("NAME", Descending: true) }, limit: 51, offset: 10),
            Metadata(),
            Decision());

        planned.Sql.ShouldBe("SELECT \"id\", \"name\" FROM sales.dbo.customers ORDER BY \"name\" DESC OFFSET 10 ROWS LIMIT 51");
    }

    [Fact]
    public void Plan_NonPositiveLimit_Throws()
    {
        Should.Throw<ArgumentException>(() => _planner.Plan(Intent(limit: 0), Metadata(), Decision()));
    }

    [Fact]
    public void Plan_ODataFilter_EmitsNamedParametersOnly()
    {
        var filter = ODataFilterParser.Parse("NAME eq 'O''Brien' and id gt 5");

        var planned = _planner.Plan(Intent(select: new[] { "id" }, filter: filter), Metadata(), Decision());

        planned.Sql.ShouldNotContain("Brien");
        planned.Sql.ShouldContain("\"name\"");   // catalog spelling, not the request spelling
        planned.Sql.ShouldContain("@p_od_0");
        planned.Sql.ShouldContain("@p_od_1");
        planned.Parameters.Values.Any(v => v is string text && text.Contains("Brien", StringComparison.Ordinal)).ShouldBeTrue();
        planned.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Plan_FilterWithoutTrinoBuilder_FailsClosed()
    {
        var filter = new TableFilterClause("name = 'x'", new Dictionary<string, object?>(), new[] { "name" });

        Should.Throw<NotSupportedException>(() => _planner.Plan(Intent(filter: filter), Metadata(), Decision()));
    }

    [Fact]
    public void PlanCount_SelectsCountUnderTheSameFilter()
    {
        var filter = ODataFilterParser.Parse("name eq 'x'");

        var planned = _planner.PlanCount(Intent(filter: filter, limit: 1), Metadata(), Decision());

        planned.Sql.ShouldBe("SELECT COUNT(*) AS \"total_count\" FROM sales.dbo.customers WHERE (\"name\" = @p_od_0)");
        planned.Parameters["@p_od_0"].ShouldBe("x");
    }

    [Fact]
    public void PlanCount_FilterOnMaskedColumn_Throws()
    {
        var filter = ODataFilterParser.Parse("email eq 'x'");

        Should.Throw<ArgumentException>(() => _planner.PlanCount(Intent(filter: filter), Metadata(), Decision()));
    }

    [Theory]
    [InlineData("name eq 'x'")]
    [InlineData("name ne null")]
    [InlineData("contains(name,'a') and id ge 3")]
    [InlineData("startswith(tolower(name),'a') or not (id lt 3)")]
    public void Plan_ODataFilterShapes_ParseWithTheTrinoGrammar(string odataFilter)
    {
        var filter = ODataFilterParser.Parse(odataFilter);
        var planned = _planner.Plan(Intent(select: new[] { "id" }, filter: filter, offset: 5), Metadata(), Decision());

        var normalized = GovernedSqlExecutionService.NormalizeClientParameters(planned.Sql, planned.Parameters, out var replaced);

        replaced.Count.ShouldBe(planned.Parameters.Count);
        Should.NotThrow(() => new FastSqlEngine().Parse(normalized.AsMemory()));
    }

    [Fact]
    public void Plan_PagedReadWithoutPrimaryKey_OrdersByAllClearOrderableColumns()
    {
        var planned = _planner.Plan(Intent(offset: 20), Metadata(primaryKey: Array.Empty<string>()), Decision());

        planned.Sql.ShouldContain("ORDER BY \"id\", \"name\" OFFSET 20 ROWS");
    }

    [Fact]
    public void Plan_UnpagedReadWithoutPrimaryKey_AddsNoOrderBy()
    {
        var planned = _planner.Plan(Intent(), Metadata(primaryKey: Array.Empty<string>()), Decision());

        planned.Sql.ShouldNotContain("ORDER BY");
    }

    [Fact]
    public void Plan_PagedReadWithoutPrimaryKeyAndWithoutClearColumn_Throws()
    {
        var decision = TableAccessDecision.Allowed(
            Customers,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Mask,
                ["email"] = ColumnAccessLevel.Mask,
                ["name"] = ColumnAccessLevel.Mask,
                ["secret"] = ColumnAccessLevel.Deny
            },
            hasUnconstrainedColumnAllow: true);

        Should.Throw<ArgumentException>(() => _planner.Plan(Intent(offset: 5), Metadata(primaryKey: Array.Empty<string>()), decision));
    }

    private static TableMetadata Unkeyed(params (string Name, string Type)[] cols) => new()
    {
        Identifier = Customers,
        Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "customers", DataSourceType = DataSourceType.Sql, IsActive = true },
        Columns = cols.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList()
    };

    private static TableAccessDecision Levels(params (string Name, ColumnAccessLevel Level)[] levels) => TableAccessDecision.Allowed(
        Customers,
        levels.ToDictionary(l => l.Name, l => l.Level, StringComparer.OrdinalIgnoreCase),
        hasUnconstrainedColumnAllow: true);

    [Fact]
    public void TableMetadata_DefaultPrimaryKey_IsEmpty() => new TableMetadata().PrimaryKeyColumns.ShouldBeEmpty();

    [Fact]
    public void Plan_UnkeyedTableTopOnly_NeverReferencesIdColumn()
    {
        var meta = Unkeyed(("code", "varchar"), ("qty", "int"));
        var planned = _planner.Plan(Intent(limit: 6), meta, Levels(("code", ColumnAccessLevel.Clear), ("qty", ColumnAccessLevel.Clear)));

        planned.Sql.ShouldNotContain("\"id\"");
        planned.Sql.ShouldNotContain("OFFSET");
    }

    [Fact]
    public void Plan_UnkeyedTableOffset_OrdersByClearOrderableColumnsInCatalogOrder()
    {
        var meta = Unkeyed(("code", "varchar(20)"), ("masked", "varchar"), ("blob", "varbinary(max)"), ("qty", "int"), ("doc", "xml"), ("hidden", "int"), ("note", "nvarchar(max)"), ("g", "geography"));
        var decision = Levels(("code", ColumnAccessLevel.Clear), ("masked", ColumnAccessLevel.Mask), ("blob", ColumnAccessLevel.Clear),
            ("qty", ColumnAccessLevel.Clear), ("doc", ColumnAccessLevel.Clear), ("hidden", ColumnAccessLevel.Deny),
            ("note", ColumnAccessLevel.Clear), ("g", ColumnAccessLevel.Clear));
        var planned = _planner.Plan(Intent(limit: 6, offset: 5), meta, decision);

        planned.Sql.ShouldContain("ORDER BY \"code\", \"qty\" OFFSET 5 ROWS");
    }

    [Fact]
    public void Plan_KeyWithMaskedColumn_IsSkippedAsAWhole()
    {
        var meta = Unkeyed(("a", "int"), ("b", "int"), ("c", "int")) with { PrimaryKeyColumns = new[] { "a", "b" } };
        var decision = Levels(("a", ColumnAccessLevel.Clear), ("b", ColumnAccessLevel.Mask), ("c", ColumnAccessLevel.Clear));
        var planned = _planner.Plan(Intent(limit: 6, offset: 5), meta, decision);

        planned.Sql.ShouldContain("ORDER BY \"a\", \"c\" OFFSET");
        planned.Sql[planned.Sql.IndexOf("ORDER BY", StringComparison.Ordinal)..].ShouldNotContain("\"b\"");
    }

    [Fact]
    public void Plan_KeyWithMaskedColumnAndNoOffset_AddsNoOrderBy()
    {
        var meta = Unkeyed(("a", "int"), ("b", "int")) with { PrimaryKeyColumns = new[] { "a", "b" } };
        var planned = _planner.Plan(Intent(limit: 6), meta, Levels(("a", ColumnAccessLevel.Clear), ("b", ColumnAccessLevel.Mask)));

        planned.Sql.ShouldNotContain("ORDER BY");
    }

    [Fact]
    public void Plan_NothingOrderable_ThrowsWithoutHiddenColumnNames()
    {
        var meta = Unkeyed(("secretcol", "int"), ("maskedcol", "int"), ("doc", "xml"));
        var decision = Levels(("secretcol", ColumnAccessLevel.Deny), ("maskedcol", ColumnAccessLevel.Mask), ("doc", ColumnAccessLevel.Clear));

        var ex = Should.Throw<ArgumentException>(() => _planner.Plan(Intent(limit: 6, offset: 5), meta, decision));
        ex.Message.ShouldNotContain("secretcol");
        ex.Message.ShouldNotContain("maskedcol");
    }

    [Fact]
    public void Plan_TableWithClientIdAndTs_AutomaticallyOrdersByCompositeClientIdTs()
    {
        var meta = Unkeyed(("client_id", "varchar"), ("ts", "timestamp"), ("value", "double"));
        var decision = Levels(
            ("client_id", ColumnAccessLevel.Clear),
            ("ts", ColumnAccessLevel.Clear),
            ("value", ColumnAccessLevel.Clear));

        var planned = _planner.Plan(Intent(limit: 6), meta, decision);

        planned.Sql.ShouldContain("ORDER BY \"client_id\", \"ts\" LIMIT 6");
    }

    [Fact]
    public void Plan_TableWithClientIdOnly_AutomaticallyOrdersByClientId()
    {
        var meta = Unkeyed(("client_id", "varchar"), ("value", "double"));
        var decision = Levels(
            ("client_id", ColumnAccessLevel.Clear),
            ("value", ColumnAccessLevel.Clear));

        var planned = _planner.Plan(Intent(limit: 6), meta, decision);

        planned.Sql.ShouldContain("ORDER BY \"client_id\" LIMIT 6");
    }

    [Fact]
    public void Plan_TableWithDeclaredUniqueIndex_OrdersByShortestClearUniqueIndex()
    {
        var meta = Unkeyed(("a", "int"), ("b", "int"), ("c", "int")) with
        {
            Indexes = new[]
            {
                new TableIndexDefinition("ux_abc", new[] { "a", "b", "c" }, IsUnique: true),
                new TableIndexDefinition("ux_ab", new[] { "a", "b" }, IsUnique: true)
            }
        };
        var decision = Levels(
            ("a", ColumnAccessLevel.Clear),
            ("b", ColumnAccessLevel.Clear),
            ("c", ColumnAccessLevel.Clear));

        var planned = _planner.Plan(Intent(limit: 6), meta, decision);

        planned.Sql.ShouldContain("ORDER BY \"a\", \"b\" LIMIT 6");
    }
}
