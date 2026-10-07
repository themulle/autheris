using System.Text.Json;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// G2/G4 (docs/plans/rls-subquery-in-strategy.md): a GraphQL selection tree (root table, nested relations, where,
/// orderBy, paging) compiles into ONE SQL statement that returns the finished JSON. Row filters, tenant filter and
/// masks apply at every level; values are bound as parameters. The SQLite tests execute the statement.
/// </summary>
public sealed class TreeSqlCompilerTests : IDisposable
{
    private static readonly TableIdentifier Authors = new("blog", "main", "authors");
    private static readonly TableIdentifier Articles = new("blog", "main", "articles");

    private readonly SqliteConnection _connection;

    public TreeSqlCompilerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT, email TEXT, tenant_id TEXT, rating INTEGER);
            CREATE TABLE articles (id INTEGER PRIMARY KEY, author_id INTEGER, title TEXT, published INTEGER, secret TEXT);
            INSERT INTO authors VALUES (1, 'Ann', 'ann@example.com', 't1', 5), (2, 'Bob', 'bob@example.com', 't1', 3),
                                       (3, 'O''Brien', 'ob@example.com', 't1', 4), (4, 'Eve', 'eve@example.com', 't2', 5);
            INSERT INTO articles VALUES (10, 1, 'A1', 1, 's10'), (11, 1, 'A2', 0, 's11'), (12, 1, 'A3', 1, 's12'),
                                        (20, 2, 'B1', 0, 's20'), (30, 3, 'C1', 1, 's30'), (40, 4, 'E1', 1, 's40');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static TableMetadata AuthorsMeta() => new()
    {
        Identifier = Authors,
        Table = new Table { SourceName = "blog", SchemaName = "main", TableName = "authors", SourceType = "Sqlite" },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "name", DataType = "text" },
            new TableColumn { ColumnName = "email", DataType = "text" },
            new TableColumn { ColumnName = "tenant_id", DataType = "text" },
            new TableColumn { ColumnName = "rating", DataType = "integer" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" } }
    };

    private static TableMetadata ArticlesMeta() => new()
    {
        Identifier = Articles,
        Table = new Table { SourceName = "blog", SchemaName = "main", TableName = "articles", SourceType = "Sqlite" },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "author_id", DataType = "integer" },
            new TableColumn { ColumnName = "title", DataType = "text" },
            new TableColumn { ColumnName = "published", DataType = "integer" },
            new TableColumn { ColumnName = "secret", DataType = "text" }
        ]
    };

    private static Dictionary<TableIdentifier, TreeTableAccess> Access(
        string? articlesRowFilter = null,
        IReadOnlyDictionary<string, object?>? articlesRowFilterParameters = null,
        string? authorsRowFilter = null,
        IReadOnlyDictionary<string, object?>? authorsRowFilterParameters = null,
        string? tenant = null)
    {
        var authorsDecision = TableAccessDecision.Allowed(Authors,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny },
            authorsRowFilter, hasUnconstrainedColumnAllow: true, authorsRowFilterParameters);
        var articlesDecision = TableAccessDecision.Allowed(Articles,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["secret"] = ColumnAccessLevel.Deny },
            articlesRowFilter, hasUnconstrainedColumnAllow: true, articlesRowFilterParameters);
        return new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Authors] = new(AuthorsMeta(), authorsDecision, tenant == null ? null : "tenant_id", tenant),
            [Articles] = new(ArticlesMeta(), articlesDecision)
        };
    }

    private static TreeRelationNode ArticlesOf(TreeQueryNode child, string key = "articles") =>
        new(key, ParentColumns: ["id"], ChildColumns: ["author_id"], IsList: true, child);

    private JsonElement Execute(CompiledTreeQuery compiled)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = compiled.Sql;
        foreach (var (name, value) in compiled.Parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        var json = (string)cmd.ExecuteScalar()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void RootWithNestedList_IsOneStatement_WithRowFilterOnTheChild()
    {
        var root = new TreeQueryNode(Authors, ["id", "name"])
        {
            Relations = [ArticlesOf(new TreeQueryNode(Articles, ["id", "title"]) { OrderBy = [new TreeOrder("id")] })],
            OrderBy = [new TreeOrder("id")],
            Limit = 2
        };

        var compiled = TreeSqlCompiler.Compile(root, Access(articlesRowFilter: "\"published\" = 1"), DatabaseDialect.Sqlite);
        var result = Execute(compiled);

        result.GetArrayLength().ShouldBe(2);
        result[0].GetProperty("id").GetInt32().ShouldBe(1);
        result[0].GetProperty("name").GetString().ShouldBe("Ann");
        result[0].GetProperty("articles").EnumerateArray().Select(a => a.GetProperty("title").GetString()).ShouldBe(new[] { "A1", "A3" }, ignoreOrder: false);
        result[1].GetProperty("articles").GetArrayLength().ShouldBe(0); // Bob only has an unpublished article
        result[0].TryGetProperty("email", out _).ShouldBeFalse();     // only selected columns
    }

    [Fact]
    public void MaskedColumn_IsMaskedInSql()
    {
        var root = new TreeQueryNode(Authors, ["id", "email"]) { OrderBy = [new TreeOrder("id")], Limit = 1 };

        var result = Execute(TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.Sqlite));

        result[0].GetProperty("email").GetString().ShouldBe("***");
    }

    [Fact]
    public void DeniedColumn_IsRejected()
    {
        var root = new TreeQueryNode(Articles, ["id", "secret"]);

        Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.Sqlite))
            .Message.ShouldContain("secret");
    }

    [Fact]
    public void UnknownColumn_IsRejected_WithTheSameMessageShapeAsDenied()
    {
        var denied = Should.Throw<GatewayInvalidQueryException>(() =>
            TreeSqlCompiler.Compile(new TreeQueryNode(Articles, ["secret"]), Access(), DatabaseDialect.Sqlite));
        var unknown = Should.Throw<GatewayInvalidQueryException>(() =>
            TreeSqlCompiler.Compile(new TreeQueryNode(Articles, ["nope"]), Access(), DatabaseDialect.Sqlite));

        denied.Message.Replace("secret", "X").ShouldBe(unknown.Message.Replace("nope", "X"));
    }

    [Theory]
    [InlineData("email")]  // masked
    [InlineData("tenant_id")] // denied
    public void FilterOrSortOnNonClearColumn_IsRejected(string column)
    {
        var filtered = new TreeQueryNode(Authors, ["id"]) { Where = new TreeComparison(column, TreeFilterOperator.Eq, "x") };
        var sorted = new TreeQueryNode(Authors, ["id"]) { OrderBy = [new TreeOrder(column)] };

        Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(filtered, Access(), DatabaseDialect.Sqlite));
        Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(sorted, Access(), DatabaseDialect.Sqlite));
    }

    [Fact]
    public void WhereValues_AreParameters_NeverSqlText()
    {
        var root = new TreeQueryNode(Authors, ["id", "name"])
        {
            Where = new TreeOrFilter(
            [
                new TreeComparison("name", TreeFilterOperator.Eq, "O'Brien"),
                new TreeComparison("name", TreeFilterOperator.StartsWith, "An%")
            ])
        };

        var compiled = TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.Sqlite);
        var result = Execute(compiled);

        compiled.Sql.ShouldNotContain("O'Brien");
        compiled.Sql.ShouldNotContain("O''Brien");
        compiled.Parameters.Values.ShouldContain("O'Brien");
        // "An%" is a literal prefix: % is escaped, so "Ann" does not match.
        result.EnumerateArray().Select(r => r.GetProperty("name").GetString()).ShouldBe(new[] { "O'Brien" });
    }

    [Fact]
    public void WhereOperators_And_Not_In_IsNull_Comparison_Work()
    {
        var root = new TreeQueryNode(Authors, ["id"])
        {
            Where = new TreeAndFilter(
            [
                new TreeComparison("rating", TreeFilterOperator.Gte, 4L),
                new TreeNotFilter(new TreeComparison("id", TreeFilterOperator.In, new object?[] { 3L })),
                new TreeComparison("name", TreeFilterOperator.IsNull, false)
            ]),
            OrderBy = [new TreeOrder("id")]
        };

        var result = Execute(TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.Sqlite));

        result.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe(new[] { 1, 4 }, ignoreOrder: false);
    }

    [Fact]
    public void OrderBy_Limit_Offset_ApplyAtTheRoot()
    {
        var root = new TreeQueryNode(Authors, ["id"]) { OrderBy = [new TreeOrder("rating", Descending: true), new TreeOrder("id")], Limit = 2, Offset = 1 };

        var result = Execute(TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.Sqlite));

        // rating desc, id asc: 1(5), 4(5), 3(4), 2(3) -> skip 1, take 2
        result.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe(new[] { 4, 3 }, ignoreOrder: false);
    }

    [Fact]
    public void ChildLimit_AppliesPerParent()
    {
        var root = new TreeQueryNode(Authors, ["id"])
        {
            Relations = [ArticlesOf(new TreeQueryNode(Articles, ["id"]) { OrderBy = [new TreeOrder("id", Descending: true)], Limit = 1 })],
            OrderBy = [new TreeOrder("id")],
            Limit = 1
        };

        var result = Execute(TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.Sqlite));

        result[0].GetProperty("articles").EnumerateArray().Select(a => a.GetProperty("id").GetInt32()).ShouldBe(new[] { 12 });
    }

    [Fact]
    public void ManyToOne_IsAnObjectOrNull()
    {
        var root = new TreeQueryNode(Articles, ["id"])
        {
            Relations = [new TreeRelationNode("author", ParentColumns: ["author_id"], ChildColumns: ["id"], IsList: false, new TreeQueryNode(Authors, ["name"]))],
            OrderBy = [new TreeOrder("id")],
            Limit = 1
        };

        var withAuthor = Execute(TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.Sqlite));
        var hiddenAuthor = Execute(TreeSqlCompiler.Compile(root, Access(authorsRowFilter: "\"id\" <> 1"), DatabaseDialect.Sqlite));

        withAuthor[0].GetProperty("author").GetProperty("name").GetString().ShouldBe("Ann");
        hiddenAuthor[0].GetProperty("author").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void TenantColumn_IsEnforcedAsParameter()
    {
        var root = new TreeQueryNode(Authors, ["id"]) { OrderBy = [new TreeOrder("id")] };

        var compiled = TreeSqlCompiler.Compile(root, Access(tenant: "t2"), DatabaseDialect.Sqlite);
        var result = Execute(compiled);

        compiled.Sql.ShouldNotContain("'t2'");
        result.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe(new[] { 4 });
    }

    [Fact]
    public void RowFilterParameters_AreBound()
    {
        var root = new TreeQueryNode(Authors, ["id"]) { OrderBy = [new TreeOrder("id")] };

        var result = Execute(TreeSqlCompiler.Compile(root,
            Access(authorsRowFilter: "\"rating\" = @rf_min", authorsRowFilterParameters: new Dictionary<string, object?> { ["@rf_min"] = 5L }),
            DatabaseDialect.Sqlite));

        result.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe(new[] { 1, 4 }, ignoreOrder: false);
    }

    [Fact]
    public void ConflictingRowFilterParameters_FailClosed()
    {
        var root = new TreeQueryNode(Authors, ["id"])
        {
            Relations = [ArticlesOf(new TreeQueryNode(Articles, ["id"]))]
        };
        var access = Access(
            authorsRowFilter: "\"rating\" = @rf", authorsRowFilterParameters: new Dictionary<string, object?> { ["@rf"] = 5L },
            articlesRowFilter: "\"published\" = @rf", articlesRowFilterParameters: new Dictionary<string, object?> { ["@rf"] = 1L });

        Should.Throw<GatewaySecurityException>(() => TreeSqlCompiler.Compile(root, access, DatabaseDialect.Sqlite));
    }

    [Fact]
    public void JoinKeyThatIsNotClear_IsRejected()
    {
        var access = Access();
        var articles = access[Articles];
        access[Articles] = articles with
        {
            Decision = TableAccessDecision.Allowed(Articles,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["author_id"] = ColumnAccessLevel.Mask },
                hasUnconstrainedColumnAllow: true)
        };
        var root = new TreeQueryNode(Authors, ["id"]) { Relations = [ArticlesOf(new TreeQueryNode(Articles, ["id"]))] };

        Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(root, access, DatabaseDialect.Sqlite));
    }

    [Fact]
    public void MissingOrDeniedTableAccess_FailsClosed()
    {
        var access = Access();
        access.Remove(Articles);
        var root = new TreeQueryNode(Authors, ["id"]) { Relations = [ArticlesOf(new TreeQueryNode(Articles, ["id"]))] };

        Should.Throw<GatewayForbiddenException>(() => TreeSqlCompiler.Compile(root, access, DatabaseDialect.Sqlite));

        var denied = Access();
        denied[Articles] = denied[Articles] with { Decision = TableAccessDecision.Denied(Articles, "no consent") };
        Should.Throw<GatewayForbiddenException>(() => TreeSqlCompiler.Compile(root, denied, DatabaseDialect.Sqlite));
    }

    [Fact]
    public void DepthAboveLimit_IsRejected()
    {
        TreeQueryNode node = new(Articles, ["id"]);
        for (var i = 0; i < TreeSqlCompiler.MaxDepth; i++)
        {
            node = new TreeQueryNode(Authors, ["id"]) { Relations = [ArticlesOf(node, $"r{i}")] };
        }

        Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(node, Access(), DatabaseDialect.Sqlite));
    }

    [Fact]
    public void HmacColumn_IsReadRawAndReportedForGatewayPseudonymization()
    {
        var access = Access();
        var meta = AuthorsMeta();
        access[Authors] = access[Authors] with
        {
            Metadata = new TableMetadata
            {
                Identifier = meta.Identifier,
                Table = meta.Table,
                Columns = meta.Columns,
                PrimaryKeyColumns = meta.PrimaryKeyColumns,
                ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "HMAC_SHA256" } }
            }
        };
        var root = new TreeQueryNode(Articles, ["id"])
        {
            Relations = [new TreeRelationNode("author", ["author_id"], ["id"], false, new TreeQueryNode(Authors, ["email"]))],
            OrderBy = [new TreeOrder("id")],
            Limit = 1
        };

        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.Sqlite);
        var result = Execute(compiled);

        compiled.HmacColumns.ShouldHaveSingleItem();
        compiled.HmacColumns[0].Path.ShouldBe(new[] { "author" }, ignoreOrder: false);
        compiled.HmacColumns[0].Column.ShouldBe("email");
        result[0].GetProperty("author").GetProperty("email").GetString().ShouldBe("ann@example.com");
    }

    [Fact]
    public void SqlServer_UsesForJsonPath_WithNestedJsonQuery()
    {
        var root = new TreeQueryNode(Authors, ["id", "name"])
        {
            Relations = [ArticlesOf(new TreeQueryNode(Articles, ["title"]) { Limit = 5 })],
            OrderBy = [new TreeOrder("id")],
            Limit = 10
        };

        var sql = TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.SqlServer).Sql;

        sql.ShouldContain("FOR JSON PATH, INCLUDE_NULL_VALUES");
        sql.ShouldContain("JSON_QUERY(");
        sql.ShouldContain("OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY");
        sql.ShouldContain("TOP (5)");
        sql.ShouldContain("[autheris_target]");
    }

    [Fact]
    public void PostgreSql_UsesJsonbAggregation()
    {
        var root = new TreeQueryNode(Authors, ["id", "name"])
        {
            Relations = [ArticlesOf(new TreeQueryNode(Articles, ["title"]))],
            OrderBy = [new TreeOrder("id")]
        };

        var sql = TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.PostgreSql).Sql;

        sql.ShouldContain("jsonb_agg(");
        sql.ShouldContain("jsonb_build_object(");
        sql.ShouldContain("\"autheris_target\"");
    }

    [Fact]
    public void WideTable_IsSplitIntoObjectChunks()
    {
        // PostgreSQL and SQLite limit the number of function arguments; objects are merged from chunks.
        var columns = Enumerable.Range(0, 120).Select(i => $"c{i}").ToArray();
        var wide = new TableIdentifier("blog", "main", "wide");
        var meta = new TableMetadata
        {
            Identifier = wide,
            Table = new Table { SourceName = "blog", SchemaName = "main", TableName = "wide", SourceType = "Sqlite" },
            Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "integer" }).ToList()
        };
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = $"CREATE TABLE wide ({string.Join(", ", columns.Select(c => c + " INTEGER"))}); INSERT INTO wide (c0, c119) VALUES (1, 119);";
            cmd.ExecuteNonQuery();
        }
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [wide] = new(meta, TableAccessDecision.Allowed(wide, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true))
        };

        var result = Execute(TreeSqlCompiler.Compile(new TreeQueryNode(wide, columns), access, DatabaseDialect.Sqlite));

        result[0].GetProperty("c0").GetInt32().ShouldBe(1);
        result[0].GetProperty("c119").GetInt32().ShouldBe(119);
        result[0].GetProperty("c50").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
