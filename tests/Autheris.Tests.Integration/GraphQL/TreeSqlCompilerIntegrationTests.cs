namespace Autheris.Tests.Integration.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.GraphQL;
using Npgsql;
using Shouldly;
using Xunit;
using BinaryOperator = TrinoSqlEngine.Ast.Nodes.BinaryOperator;

/// <summary>
/// PostgreSQL execution tests for <see cref="TreeSqlCompiler"/>.
/// </summary>
public sealed class TreeSqlCompilerIntegrationTests
{
    private static readonly TableIdentifier Authors = new("blog", "public", "authors");
    private static readonly TableIdentifier Articles = new("blog", "public", "articles");

    private static async Task SeedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE public.authors (id integer PRIMARY KEY, name text, email text, tenant_id text, rating integer);
            CREATE TABLE public.articles (id integer PRIMARY KEY, author_id integer, title text, published integer, secret text);
            INSERT INTO public.authors VALUES
                (1, 'Ann', 'ann@example.com', 't1', 5),
                (2, 'Bob', 'bob@example.com', 't1', 3),
                (3, 'O''Brien', 'ob@example.com', 't1', 4),
                (4, 'Eve', 'eve@example.com', 't2', 5);
            INSERT INTO public.articles VALUES
                (10, 1, 'A1', 1, 's10'),
                (11, 1, 'A2', 0, 's11'),
                (12, 1, 'A3', 1, 's12'),
                (20, 2, 'B1', 0, 's20'),
                (30, 3, 'C1', 1, 's30'),
                (40, 4, 'E1', 1, 's40');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static TableMetadata AuthorsMeta() => new()
    {
        Identifier = Authors,
        Table = new Table { SourceName = "blog", SchemaName = "public", TableName = "authors", SourceType = "PostgreSql" },
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
        Table = new Table { SourceName = "blog", SchemaName = "public", TableName = "articles", SourceType = "PostgreSql" },
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
        IRowPolicy? articlesRowFilter = null,
        IRowPolicy? authorsRowFilter = null,
        string? tenant = null)
    {
        var authorsDecision = TypedTreeRowPolicy.Allowed(
            Authors,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny },
            authorsRowFilter);
        var articlesDecision = TypedTreeRowPolicy.Allowed(
            Articles,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["secret"] = ColumnAccessLevel.Deny },
            articlesRowFilter);
        return new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Authors] = new(AuthorsMeta(), authorsDecision, tenant == null ? null : "tenant_id", tenant),
            [Articles] = new(ArticlesMeta(), articlesDecision)
        };
    }

    private static TreeRelationNode ArticlesOf(TreeQueryNode child, string key = "articles") =>
        new(key, ParentColumns: ["id"], ChildColumns: ["author_id"], IsList: true, child);

    private static async Task<JsonElement> ExecuteAsync(string connectionString, CompiledTreeQuery compiled)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = compiled.Sql;
        foreach (var (name, value) in compiled.Parameters)
        {
            cmd.Parameters.AddWithValue(name.TrimStart('@'), value ?? DBNull.Value);
        }

        var json = (string)(await cmd.ExecuteScalarAsync())!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [RequiresContainerRuntimeFact]
    public async Task RootWithNestedList_IsOneStatement_WithRowFilterOnTheChild()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var root = new TreeQueryNode(Authors, ["id", "name"])
        {
            Relations = [ArticlesOf(new TreeQueryNode(Articles, ["id", "title"]) { OrderBy = [new TreeOrder("id")] })],
            OrderBy = [new TreeOrder("id")],
            Limit = 2
        };

        var compiled = TreeSqlCompiler.Compile(root, Access(articlesRowFilter: TypedTreeRowPolicy.Int("published", BinaryOperator.Equal, 1)), DatabaseDialect.PostgreSql);
        var result = await ExecuteAsync(db.ConnectionString, compiled);

        result.GetArrayLength().ShouldBe(2);
        result[0].GetProperty("id").GetInt32().ShouldBe(1);
        result[0].GetProperty("name").GetString().ShouldBe("Ann");
        result[0].GetProperty("articles").EnumerateArray().Select(a => a.GetProperty("title").GetString()).ShouldBe(["A1", "A3"], ignoreOrder: false);
        result[1].GetProperty("articles").GetArrayLength().ShouldBe(0);
        result[0].TryGetProperty("email", out _).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    public async Task MaskedColumn_IsMaskedInSql()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var root = new TreeQueryNode(Authors, ["id", "email"]) { OrderBy = [new TreeOrder("id")], Limit = 1 };
        var result = await ExecuteAsync(db.ConnectionString, TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.PostgreSql));

        result[0].GetProperty("email").GetString().ShouldBe("***");
    }

    [RequiresContainerRuntimeFact]
    public async Task WhereValues_AreParameters_NeverSqlText()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var root = new TreeQueryNode(Authors, ["id", "name"])
        {
            Where = new TreeOrFilter(
            [
                new TreeComparison("name", TreeFilterOperator.Eq, "O'Brien"),
                new TreeComparison("name", TreeFilterOperator.StartsWith, "An%")
            ])
        };

        var compiled = TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.PostgreSql);
        var result = await ExecuteAsync(db.ConnectionString, compiled);

        compiled.Sql.ShouldNotContain("O'Brien");
        compiled.Sql.ShouldNotContain("O''Brien");
        compiled.Parameters.Values.ShouldContain("O'Brien");
        result.EnumerateArray().Select(r => r.GetProperty("name").GetString()).ShouldBe(["O'Brien"]);
    }

    [RequiresContainerRuntimeFact]
    public async Task WhereOperators_And_Not_In_IsNull_Comparison_Work()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

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

        var result = await ExecuteAsync(db.ConnectionString, TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.PostgreSql));

        result.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe([1, 4], ignoreOrder: false);
    }

    [RequiresContainerRuntimeFact]
    public async Task OrderBy_Limit_Offset_ApplyAtTheRoot()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var root = new TreeQueryNode(Authors, ["id"]) { OrderBy = [new TreeOrder("rating", Descending: true), new TreeOrder("id")], Limit = 2, Offset = 1 };
        var result = await ExecuteAsync(db.ConnectionString, TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.PostgreSql));

        result.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe([4, 3], ignoreOrder: false);
    }

    [RequiresContainerRuntimeFact]
    public async Task ChildLimit_AppliesPerParent()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var root = new TreeQueryNode(Authors, ["id"])
        {
            Relations = [ArticlesOf(new TreeQueryNode(Articles, ["id"]) { OrderBy = [new TreeOrder("id", Descending: true)], Limit = 1 })],
            OrderBy = [new TreeOrder("id")],
            Limit = 1
        };

        var result = await ExecuteAsync(db.ConnectionString, TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.PostgreSql));

        result[0].GetProperty("articles").EnumerateArray().Select(a => a.GetProperty("id").GetInt32()).ShouldBe([12]);
    }

    [RequiresContainerRuntimeFact]
    public async Task ManyToOne_IsAnObjectOrNull()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var root = new TreeQueryNode(Articles, ["id"])
        {
            Relations = [new TreeRelationNode("author", ParentColumns: ["author_id"], ChildColumns: ["id"], IsList: false, new TreeQueryNode(Authors, ["name"]))],
            OrderBy = [new TreeOrder("id")],
            Limit = 1
        };

        var withAuthor = await ExecuteAsync(db.ConnectionString, TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.PostgreSql));
        var hiddenAuthor = await ExecuteAsync(db.ConnectionString, TreeSqlCompiler.Compile(root, Access(authorsRowFilter: TypedTreeRowPolicy.Int("id", BinaryOperator.NotEqual, 1)), DatabaseDialect.PostgreSql));

        withAuthor[0].GetProperty("author").GetProperty("name").GetString().ShouldBe("Ann");
        hiddenAuthor[0].GetProperty("author").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [RequiresContainerRuntimeFact]
    public async Task TenantColumn_IsEnforcedAsParameter()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var root = new TreeQueryNode(Authors, ["id"]) { OrderBy = [new TreeOrder("id")] };
        var compiled = TreeSqlCompiler.Compile(root, Access(tenant: "t2"), DatabaseDialect.PostgreSql);
        var result = await ExecuteAsync(db.ConnectionString, compiled);

        compiled.Sql.ShouldNotContain("'t2'");
        result.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe([4]);
    }

    [RequiresContainerRuntimeFact]
    public async Task RowFilterParameters_AreBound()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var root = new TreeQueryNode(Authors, ["id"]) { OrderBy = [new TreeOrder("id")] };
        var result = await ExecuteAsync(
            db.ConnectionString,
            TreeSqlCompiler.Compile(root, Access(authorsRowFilter: TypedTreeRowPolicy.Int("rating", BinaryOperator.Equal, 5)), DatabaseDialect.PostgreSql));

        result.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe([1, 4], ignoreOrder: false);
    }

    [RequiresContainerRuntimeFact]
    public async Task SameRowFilterParameterName_OnTwoTables_IsBoundPerTable()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

        var root = new TreeQueryNode(Authors, ["id"])
        {
            OrderBy = [new TreeOrder("id")],
            Relations = [ArticlesOf(new TreeQueryNode(Articles, ["id"]) { OrderBy = [new TreeOrder("id")] })]
        };
        var access = Access(
            authorsRowFilter: TypedTreeRowPolicy.Int("rating", BinaryOperator.Equal, 5),
            articlesRowFilter: TypedTreeRowPolicy.Int("published", BinaryOperator.Equal, 1));

        var result = await ExecuteAsync(db.ConnectionString, TreeSqlCompiler.Compile(root, access, DatabaseDialect.PostgreSql));

        var authors = result.EnumerateArray().ToList();
        authors.Select(a => a.GetProperty("id").GetInt32()).ShouldBe([1, 4]);
        authors[0].GetProperty("articles").EnumerateArray().Select(a => a.GetProperty("id").GetInt32()).ShouldBe([10, 12]);
    }

    [RequiresContainerRuntimeFact]
    public async Task HmacColumn_IsReadRawAndReportedForGatewayPseudonymization()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);

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

        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.PostgreSql);
        var result = await ExecuteAsync(db.ConnectionString, compiled);

        compiled.HmacColumns.ShouldHaveSingleItem();
        compiled.HmacColumns[0].Path.ShouldBe(["author"], ignoreOrder: false);
        compiled.HmacColumns[0].Column.ShouldBe("email");
        result[0].GetProperty("author").GetProperty("email").GetString().ShouldBe("ann@example.com");
    }

    [RequiresContainerRuntimeFact]
    public async Task WideTable_IsSplitIntoObjectChunks()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        var columns = Enumerable.Range(0, 120).Select(i => $"c{i}").ToArray();
        var wide = new TableIdentifier("blog", "public", "wide");
        var meta = new TableMetadata
        {
            Identifier = wide,
            Table = new Table { SourceName = "blog", SchemaName = "public", TableName = "wide", SourceType = "PostgreSql" },
            Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "integer" }).ToList()
        };
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE TABLE public.wide ({string.Join(", ", columns.Select(c => c + " integer"))}); INSERT INTO public.wide (c0, c119) VALUES (1, 119);";
            await cmd.ExecuteNonQueryAsync();
        }

        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [wide] = new(meta, TableAccessDecision.Allowed(wide, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true))
        };

        var result = await ExecuteAsync(db.ConnectionString, TreeSqlCompiler.Compile(new TreeQueryNode(wide, columns), access, DatabaseDialect.PostgreSql));

        result[0].GetProperty("c0").GetInt32().ShouldBe(1);
        result[0].GetProperty("c119").GetInt32().ShouldBe(119);
        result[0].GetProperty("c50").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
