namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #836: the set-based tree root orders its page by the clear unique key as tie-break. The page CTE must project every
/// tie-break column (also when the caller neither selects nor orders by it), exactly once, and never return it in the JSON.
/// </summary>
public sealed class Issue836SetBasedTreePageTieBreakTests
{
    private static readonly TableIdentifier Cranes = new("test", "main", "cranes");
    private static readonly TableIdentifier Readings = new("test", "main", "readings");

    private static readonly TableIndexDefinition AssetTagIndex = new("ux_asset_tag", ["asset_tag"], true, [KeyColumnDirection.Ascending]);

    private static TableMetadata CranesMeta(
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition>? indexes = null,
        string? extraColumn = null) => new()
    {
        Identifier = Cranes,
        Table = new Table { SourceName = "test", SchemaName = "main", TableName = "cranes", SourceType = "PostgreSql" },
        PrimaryKeyColumns = [.. primaryKey],
        Indexes = indexes ?? [],
        Columns =
        [
            new TableColumn { ColumnName = "site", DataType = "text" },
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "crane_serial_number", DataType = "text" },
            new TableColumn { ColumnName = "client_id", DataType = "integer" },
            new TableColumn { ColumnName = "asset_tag", DataType = "text" },
            .. extraColumn is null ? Array.Empty<TableColumn>() : [new TableColumn { ColumnName = extraColumn, DataType = "text" }]
        ]
    };

    private static TableMetadata ReadingsMeta(string primaryKey = "id") => new()
    {
        Identifier = Readings,
        Table = new Table { SourceName = "test", SchemaName = "main", TableName = "readings", SourceType = "PostgreSql" },
        PrimaryKeyColumns = [primaryKey],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "crane_serial_number", DataType = "text" },
            new TableColumn { ColumnName = "ts", DataType = "text" },
            new TableColumn { ColumnName = "value", DataType = "text" }
        ]
    };

    /// <summary>Column access maps are ordinal (case-sensitive) dictionaries.</summary>
    private static Dictionary<TableIdentifier, TreeTableAccess> Access(
        TableMetadata cranes,
        IReadOnlyDictionary<string, ColumnAccessLevel>? craneColumns = null,
        TableMetadata? readings = null,
        IReadOnlyDictionary<string, ColumnAccessLevel>? readingColumns = null,
        TreeSecuredSource? craneSource = null) => new()
    {
        [Cranes] = new(cranes,
            TypedTreeRowPolicy.Allowed(Cranes, craneColumns ?? new Dictionary<string, ColumnAccessLevel>(StringComparer.Ordinal), null),
            SecuredSource: craneSource),
        [Readings] = new(readings ?? ReadingsMeta(),
            TypedTreeRowPolicy.Allowed(Readings, readingColumns ?? new Dictionary<string, ColumnAccessLevel>(StringComparer.Ordinal), null))
    };

    private static Dictionary<string, ColumnAccessLevel> Columns(string column, ColumnAccessLevel level) =>
        new(StringComparer.Ordinal) { [column] = level };

    /// <summary>The reported shape: root ordered by a non-key column, key not selected, one set-based list child.</summary>
    private static TreeQueryNode Root(IReadOnlyList<string> columns, params TreeOrder[] orderBy) =>
        new(Cranes, columns)
        {
            Relations =
            [
                new TreeRelationNode("readings", ["crane_serial_number"], ["crane_serial_number"], IsList: true,
                    new TreeQueryNode(Readings, ["ts", "value"]) { OrderBy = [new TreeOrder("ts", Descending: true)], Limit = 1 })
            ],
            OrderBy = orderBy.Length == 0 ? [new TreeOrder("client_id")] : [.. orderBy],
            Limit = 5
        };

    private static CompiledTreeQuery CompileSetBased(TreeQueryNode root, Dictionary<TableIdentifier, TreeTableAccess> access, DatabaseDialect dialect)
    {
        var compiled = TreeSqlCompiler.Compile(root, access, dialect);
        compiled.Lists.Single().Strategy.ShouldBe(TreeListStrategy.SetBased);
        return compiled;
    }

    /// <summary>Output aliases of the page CTE select list (<c>autheris_page AS (SELECT … FROM</c>).</summary>
    private static List<string> PageCteProjection(string sql)
    {
        var start = sql.IndexOf("autheris_page", StringComparison.Ordinal);
        start = sql.IndexOf("AS (SELECT ", start, StringComparison.Ordinal) + "AS (SELECT ".Length;
        var end = sql.IndexOf(" FROM ", start, StringComparison.Ordinal);
        return Regex.Matches(sql[start..end], @" AS [\[""]([^\]""]+)[\]""]").Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>The root statement after the CTE list.</summary>
    private static string RootQuery(string sql) => sql[(sql.LastIndexOf("\nSELECT ", StringComparison.Ordinal) + 1)..];

    /// <summary>Columns of the outer root ORDER BY (SQL Server FOR JSON, PostgreSQL jsonb_agg).</summary>
    private static List<string> OuterOrderColumns(string sql)
    {
        var root = RootQuery(sql);
        var start = root.LastIndexOf("ORDER BY ", StringComparison.Ordinal) + "ORDER BY ".Length;
        var end = root.IndexOf(root.Contains(" FOR JSON PATH", StringComparison.Ordinal) ? " FOR JSON PATH" : "), ", start, StringComparison.Ordinal);
        return Regex.Matches(root[start..end], @"\.[\[""]([^\]""]+)[\]""]").Select(m => m.Groups[1].Value).ToList();
    }

    private static void AssertOuterOrderIsProjected(string sql)
    {
        var projected = PageCteProjection(sql);
        foreach (var column in OuterOrderColumns(sql))
        {
            projected.ShouldContain(column, $"outer ORDER BY column '{column}' is not projected by the page CTE: {sql}");
        }

        projected.Count.ShouldBe(projected.Distinct(StringComparer.OrdinalIgnoreCase).Count(), $"duplicate page CTE column: {sql}");
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void SingleColumnKey_NotSelectedNotOrdered_IsProjectedInPageCte(DatabaseDialect dialect)
    {
        var compiled = CompileSetBased(Root(["crane_serial_number", "client_id"]), Access(CranesMeta(["id"])), dialect);

        OuterOrderColumns(compiled.Sql).ShouldBe(["client_id", "id"]);
        PageCteProjection(compiled.Sql).ShouldBe(["crane_serial_number", "client_id", "id"]);
        AssertOuterOrderIsProjected(compiled.Sql);
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void CompositeKey_NotSelectedNotOrdered_AllColumnsProjectedInPageCte(DatabaseDialect dialect)
    {
        var compiled = CompileSetBased(Root(["crane_serial_number", "client_id"]), Access(CranesMeta(["site", "id"])), dialect);

        OuterOrderColumns(compiled.Sql).ShouldBe(["client_id", "site", "id"]);
        PageCteProjection(compiled.Sql).ShouldBe(["crane_serial_number", "client_id", "site", "id"]);
        AssertOuterOrderIsProjected(compiled.Sql);
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void CompositeKey_OnePartOrdered_OtherPartProjectedOnce(DatabaseDialect dialect)
    {
        var compiled = CompileSetBased(
            Root(["crane_serial_number"], new TreeOrder("id", Descending: true)),
            Access(CranesMeta(["site", "id"])),
            dialect);

        OuterOrderColumns(compiled.Sql).ShouldBe(["id", "site"]);
        PageCteProjection(compiled.Sql).ShouldBe(["crane_serial_number", "id", "site"]);
        AssertOuterOrderIsProjected(compiled.Sql);
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void Key_Selected_IsProjectedExactlyOnce(DatabaseDialect dialect)
    {
        var compiled = CompileSetBased(Root(["id", "crane_serial_number", "client_id"]), Access(CranesMeta(["id"])), dialect);

        OuterOrderColumns(compiled.Sql).ShouldBe(["client_id", "id"]);
        PageCteProjection(compiled.Sql).ShouldBe(["id", "crane_serial_number", "client_id"]);
        AssertOuterOrderIsProjected(compiled.Sql);
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void Key_InRootOrder_IsProjectedExactlyOnce(DatabaseDialect dialect)
    {
        var compiled = CompileSetBased(
            Root(["crane_serial_number"], new TreeOrder("client_id"), new TreeOrder("id")),
            Access(CranesMeta(["id"])),
            dialect);

        OuterOrderColumns(compiled.Sql).ShouldBe(["client_id", "id"]);
        PageCteProjection(compiled.Sql).ShouldBe(["crane_serial_number", "client_id", "id"]);
        AssertOuterOrderIsProjected(compiled.Sql);
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void KeySpelledDifferentlyInMetadata_UsesCanonicalColumnNameOnce(DatabaseDialect dialect)
    {
        // The key list spells the column "ID"; the catalog column is "id". The selected "id" and the tie-break are one column.
        var selected = CompileSetBased(Root(["id", "crane_serial_number"]), Access(CranesMeta(["ID"])), dialect);
        PageCteProjection(selected.Sql).ShouldBe(["id", "crane_serial_number", "client_id"]);
        OuterOrderColumns(selected.Sql).ShouldBe(["client_id", "id"]);

        var notSelected = CompileSetBased(Root(["crane_serial_number"]), Access(CranesMeta(["ID"])), dialect);
        PageCteProjection(notSelected.Sql).ShouldBe(["crane_serial_number", "client_id", "id"]);
        OuterOrderColumns(notSelected.Sql).ShouldBe(["client_id", "id"]);
        notSelected.Sql.ShouldNotContain(dialect == DatabaseDialect.SqlServer ? "[ID]" : "\"ID\"", Case.Sensitive);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void TieBreakColumn_NotSelected_IsNotReturnedInJson(DatabaseDialect dialect)
    {
        var compiled = CompileSetBased(Root(["crane_serial_number", "client_id"]), Access(CranesMeta(["site", "id"])), dialect);
        var root = RootQuery(compiled.Sql);
        var projection = root[..root.IndexOf(" FROM ", StringComparison.Ordinal)];

        if (dialect == DatabaseDialect.SqlServer)
        {
            projection.ShouldNotContain("AS [id]");
            projection.ShouldNotContain("AS [site]");
            projection.ShouldContain("[t1].[crane_serial_number] AS [crane_serial_number]");
        }
        else
        {
            projection.ShouldNotContain("'id'");
            projection.ShouldNotContain("'site'");
            projection.ShouldContain("'crane_serial_number', \"t1\".\"crane_serial_number\"");
        }
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void MaskedPrimaryKey_IsNeverProjected_ClearUniqueIndexIsTheTieBreak(DatabaseDialect dialect)
    {
        // "id" is selected but masked: a literal in the JSON, never a page column and never the tie-break. asset_tag is
        // neither selected nor the join column, so only the tie-break logic can bring it into the page CTE.
        var access = Access(CranesMeta(["id"], [AssetTagIndex]), Columns("id", ColumnAccessLevel.Mask));

        var compiled = CompileSetBased(Root(["id", "client_id"]), access, dialect);

        AssertAssetTagIsTheTieBreak(compiled.Sql, dialect);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void DeniedPrimaryKey_IsNeverProjected_ClearUniqueIndexIsTheTieBreak(DatabaseDialect dialect)
    {
        var access = Access(CranesMeta(["id"], [AssetTagIndex]), Columns("id", ColumnAccessLevel.Deny));

        var compiled = CompileSetBased(Root(["client_id"]), access, dialect);

        AssertAssetTagIsTheTieBreak(compiled.Sql, dialect);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void KeySpelledDifferently_MaskOnCatalogSpelling_RejectsTheKey(DatabaseDialect dialect)
    {
        // The key list says "ID"; the ordinal access map masks the catalog column "id". Access is checked on the catalog name.
        var access = Access(CranesMeta(["ID"], [AssetTagIndex]), Columns("id", ColumnAccessLevel.Mask));

        var compiled = CompileSetBased(Root(["client_id"]), access, dialect);

        AssertAssetTagIsTheTieBreak(compiled.Sql, dialect);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer, "no_such_column")]
    [InlineData(DatabaseDialect.PostgreSql, "no_such_column")]
    [InlineData(DatabaseDialect.SqlServer, "bad-key")]
    [InlineData(DatabaseDialect.PostgreSql, "bad-key")]
    public void UnknownOrUnsafeKeyColumn_DisqualifiesTheKey_FallsBackToClearUniqueIndex(DatabaseDialect dialect, string keyColumn)
    {
        // "no_such_column" is not in the catalog; "bad-key" is a catalog column whose name is not a safe identifier.
        var access = Access(CranesMeta([keyColumn], [AssetTagIndex], extraColumn: keyColumn == "bad-key" ? keyColumn : null));

        var compiled = CompileSetBased(Root(["client_id"]), access, dialect);

        AssertAssetTagIsTheTieBreak(compiled.Sql, dialect);
        compiled.Sql.ShouldNotContain(keyColumn);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("no_such_column")]
    [InlineData("bad-key")]
    public void UnknownOrUnsafeKeyColumn_NoOtherClearKey_FallsBackCorrelated(string keyColumn)
    {
        var access = Access(CranesMeta([keyColumn], extraColumn: keyColumn == "bad-key" ? keyColumn : null));

        var compiled = TreeSqlCompiler.Compile(Root(["client_id"]), access, DatabaseDialect.SqlServer);

        compiled.Lists.Single().Strategy.ShouldBe(TreeListStrategy.CorrelatedUnindexed);
        compiled.Lists.Single().Reason.ShouldBe("ParentOrderNotTotal");
        compiled.Sql.ShouldNotContain("autheris_page");
        compiled.Sql.ShouldNotContain(keyColumn);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void ChildKeySpelledDifferently_MaskOnCatalogSpelling_IsNotARankingTieBreak(DatabaseDialect dialect)
    {
        var access = Access(CranesMeta(["id"]), readings: ReadingsMeta(primaryKey: "ID"), readingColumns: Columns("id", ColumnAccessLevel.Mask));

        var compiled = CompileSetBased(Root(["client_id"]), access, dialect);

        var ts = dialect == DatabaseDialect.SqlServer ? "[autheris_target].[ts]" : "\"autheris_target\".\"ts\"";
        compiled.Sql.ShouldContain($"ORDER BY {ts} DESC) AS ");
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void SecuredSourceRoot_TieBreakIsProjectedFromTheSubquery(DatabaseDialect dialect)
    {
        const string securedSql = "SELECT site, id, crane_serial_number, client_id, asset_tag FROM secured_cranes";
        var source = new TreeSecuredSource(
            securedSql,
            System.Collections.Immutable.ImmutableArray<TrinoSqlEngine.Ast.Emit.BoundParameter>.Empty,
            dialect == DatabaseDialect.SqlServer ? TrinoSqlEngine.TargetSqlDialect.SqlServer : TrinoSqlEngine.TargetSqlDialect.PostgreSql);

        var compiled = CompileSetBased(Root(["crane_serial_number", "client_id"]), Access(CranesMeta(["id"]), craneSource: source), dialect);

        var target = dialect == DatabaseDialect.SqlServer ? "[autheris_target]" : "\"autheris_target\"";
        var id = dialect == DatabaseDialect.SqlServer ? "[id]" : "\"id\"";
        compiled.Sql.ShouldContain($"{target}.{id} AS {id} FROM ({securedSql}) AS {target}");
        PageCteProjection(compiled.Sql).ShouldBe(["crane_serial_number", "client_id", "id"]);
        AssertOuterOrderIsProjected(compiled.Sql);
    }

    /// <summary>The clear unique index on asset_tag is the tie-break, it is projected into the page, and no id column is.</summary>
    private static void AssertAssetTagIsTheTieBreak(string sql, DatabaseDialect dialect)
    {
        OuterOrderColumns(sql).ShouldBe(["client_id", "asset_tag"]);
        PageCteProjection(sql).ShouldBe(["client_id", "crane_serial_number", "asset_tag"]);
        AssertOuterOrderIsProjected(sql);
        var pageCte = sql[..sql.IndexOf("autheris_k", StringComparison.Ordinal)];
        pageCte.ShouldNotContain(dialect == DatabaseDialect.SqlServer ? ".[id]" : ".\"id\"", Case.Insensitive);
    }
}
