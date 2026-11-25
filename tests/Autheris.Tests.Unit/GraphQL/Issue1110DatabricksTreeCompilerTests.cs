#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Dialects;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.Infrastructure.Databricks;
using Autheris.Tests.Unit.Databricks;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using BinaryOperator = TrinoSqlEngine.Ast.Nodes.BinaryOperator;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class Issue1110DatabricksTreeCompilerTests
{
    private static readonly TableIdentifier Authors = new("lake", "main", "authors");
    private static readonly TableIdentifier Articles = new("lake", "main", "articles");
    private static readonly TableIdentifier Tags = new("lake", "main", "tags");
    private static readonly PolicyPredicateRenderer Renderer = new();

    private static TableMetadata AuthorsMeta() => new()
    {
        Identifier = Authors,
        Table = new Table { SourceName = "lake", SchemaName = "main", TableName = "authors", SourceType = "Databricks", DataSourceType = DataSourceType.Sql, IsActive = true },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "string" },
            new TableColumn { ColumnName = "email", DataType = "string" },
            new TableColumn { ColumnName = "secret_num", DataType = "int" },
            new TableColumn { ColumnName = "tenant_id", DataType = "string" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "${spark.sql.secret}\\masked" },
            ["secret_num"] = new MaskingRule { RuleType = "NULLIFY" }
        }
    };

    private static TableMetadata ArticlesMeta() => new()
    {
        Identifier = Articles,
        Table = new Table { SourceName = "lake", SchemaName = "main", TableName = "articles", SourceType = "Databricks", DataSourceType = DataSourceType.Sql, IsActive = true },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "author_id", DataType = "int" },
            new TableColumn { ColumnName = "title", DataType = "string" },
            new TableColumn { ColumnName = "region", DataType = "string" },
            new TableColumn { ColumnName = "tenant_id", DataType = "string" }
        ]
    };

    private static TableMetadata TagsMeta() => new()
    {
        Identifier = Tags,
        Table = new Table { SourceName = "lake", SchemaName = "main", TableName = "tags", SourceType = "Databricks", DataSourceType = DataSourceType.Sql, IsActive = true },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "article_id", DataType = "int" },
            new TableColumn { ColumnName = "label", DataType = "string" },
            new TableColumn { ColumnName = "tenant_id", DataType = "string" }
        ]
    };

    private static Dictionary<TableIdentifier, TreeTableAccess> CreateAccess(
        IRowPolicy? authorsPolicy = null,
        IRowPolicy? articlesPolicy = null,
        string? tenant = "acme",
        ColumnAccessLevel emailAccess = ColumnAccessLevel.Clear,
        ColumnAccessLevel secretNumAccess = ColumnAccessLevel.Clear)
    {
        var authorsAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenant_id"] = ColumnAccessLevel.Deny,
            ["email"] = emailAccess,
            ["secret_num"] = secretNumAccess
        };
        var articlesAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny };
        var tagsAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny };

        return new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Authors] = new(AuthorsMeta(), AllowDecision(Authors, authorsAccess, authorsPolicy), tenant == null ? null : "tenant_id", tenant),
            [Articles] = new(ArticlesMeta(), AllowDecision(Articles, articlesAccess, articlesPolicy), tenant == null ? null : "tenant_id", tenant),
            [Tags] = new(TagsMeta(), AllowDecision(Tags, tagsAccess, null), tenant == null ? null : "tenant_id", tenant)
        };
    }

    private static TableAccessDecision AllowDecision(
        TableIdentifier table,
        IReadOnlyDictionary<string, ColumnAccessLevel> cols,
        IRowPolicy? policy)
    {
        var decision = TableAccessDecision.Allowed(table, cols, hasUnconstrainedColumnAllow: true);
        return policy == null ? decision : decision.WithAdditionalRowPolicy(policy);
    }

    private static IRowPolicy DatabricksEqPolicy(string column, string value)
    {
        var namer = new PolicyParameterNamer("__rls_");
        var param = namer.Add(new PolicyValue(value, SqlParameterType.String));
        var expr = new BinaryExpression(PolicyParameterNamer.Column(column), BinaryOperator.Equal, param);
        return new PolicyPredicateRowPolicy(PolicyPredicate.Create(expr, namer.Values), TargetSqlDialect.Databricks, Renderer);
    }

    [Fact]
    public async Task DatabricksDialect_SupportsTreeCompile_AndCatalogSchemaExposesNestedRelations()
    {
        TreeSqlCompiler.SupportsTreeCompile(DatabaseDialect.Databricks).ShouldBeTrue();
        ISqlDialectRegistry.Default.GetProvider(DatabaseDialect.Databricks).Descriptor.SupportsTreeCompile.ShouldBeTrue();

        var authors = AuthorsMeta();
        var articles = ArticlesMeta();
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.StubAllTables([authors, articles]);
        var relRepo = Substitute.For<ITableRelationRepository>();
        relRepo.GetRelationsForTableAsync(Authors, Arg.Any<CancellationToken>()).Returns([
            new TableRelation
            {
                RelationName = "articles",
                ParentTableIdentifier = Authors,
                ChildTableIdentifier = Articles,
                Cardinality = RelationCardinality.OneToMany,
                JoinKeysParent = ["id"],
                JoinKeysChild = ["author_id"]
            }
        ]);
        relRepo.GetRelationsForTableAsync(Articles, Arg.Any<CancellationToken>()).Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);

        model.OmittedTables.ShouldBeEmpty();
        var authorType = model.Tables.Single(t => t.Identifier.Equals(Authors));
        authorType.Relations.Select(r => r.FieldName).ShouldBe(["articles"]);
    }

    [Fact]
    public void Databricks_CompilesCorrelatedOneToMany_ManyToOne_AndThreeLevelTrees_WithProjectionBeforeAggregate()
    {
        var tree = new TreeQueryNode(Authors, ["id", "name"])
        {
            OrderBy = [new TreeOrder("id")],
            Limit = 10,
            Relations =
            [
                new TreeRelationNode("articles", ["id"], ["author_id"], IsList: true,
                    new TreeQueryNode(Articles, ["id", "title"])
                    {
                        OrderBy = [new TreeOrder("title", Descending: true)],
                        Limit = 5,
                        Relations =
                        [
                            new TreeRelationNode("tags", ["id"], ["article_id"], IsList: true,
                                new TreeQueryNode(Tags, ["label"]) { Limit = 3 })
                        ]
                    })
            ]
        };

        var compiled = TreeSqlCompiler.Compile(tree, CreateAccess(), DatabaseDialect.Databricks);

        compiled.Sql.ShouldContain("to_json(");
        compiled.Sql.ShouldContain("map('ignoreNullFields', 'false')");
        compiled.Sql.ShouldContain("named_struct(");
        compiled.Sql.ShouldContain("array_sort(");
        compiled.Sql.ShouldContain("slice(");
        compiled.Sql.ShouldContain("transform(");
        compiled.Sql.ShouldContain("`autheris_target`");
        compiled.Sql.ShouldNotContain("FOR JSON PATH");
        compiled.Sql.ShouldNotContain("jsonb_agg");

        // Singular N:1 uses try_element_at(..., 1)
        var singular = new TreeQueryNode(Articles, ["id", "title"])
        {
            Limit = 5,
            Relations = [new TreeRelationNode("author", ["author_id"], ["id"], IsList: false, new TreeQueryNode(Authors, ["name"]))]
        };
        var compiledSingular = TreeSqlCompiler.Compile(singular, CreateAccess(), DatabaseDialect.Databricks);
        compiledSingular.Sql.ShouldContain("try_element_at(");
    }

    [Fact]
    public void Databricks_SetBasedListStrategy_GeneratesCteWithSortArrayOnRowNumber()
    {
        var tree = new TreeQueryNode(Authors, ["id", "name"])
        {
            OrderBy = [new TreeOrder("id")],
            Limit = 10,
            Relations =
            [
                new TreeRelationNode("articles", ["id"], ["author_id"], IsList: true,
                    new TreeQueryNode(Articles, ["id", "title"])
                    {
                        OrderBy = [new TreeOrder("title")],
                        Limit = 5
                    })
            ]
        };

        var compiled = TreeSqlCompiler.Compile(tree, CreateAccess(), DatabaseDialect.Databricks);

        compiled.Lists.Count.ShouldBe(1);
        compiled.Lists[0].Strategy.ShouldBe(TreeListStrategy.SetBased);
        compiled.Sql.ShouldContain("WITH `autheris_page` AS (");
        compiled.Sql.ShouldContain("`autheris_k0` AS (");
        compiled.Sql.ShouldContain("ROW_NUMBER() OVER");
        compiled.Sql.ShouldContain("array_sort(collect_list(named_struct('__rn'");
        compiled.Sql.ShouldContain("coalesce(`autheris_k0`.`autheris_json`, array())");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void BC_1110_01_Databricks_TenantFilter_UsesBinaryCast()
    {
        var compiled = TreeSqlCompiler.Compile(new TreeQueryNode(Authors, ["id"]) { Limit = 5 }, CreateAccess(tenant: "AcMe"), DatabaseDialect.Databricks);

        compiled.Sql.ShouldContain("CAST(`autheris_target`.`tenant_id` AS BINARY) = CAST(:gq0 AS BINARY)");
        compiled.Parameters[":gq0"].ShouldBe("AcMe");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void BC_1110_02_Databricks_MasksBindStringReplacementsAndCastNull()
    {
        var compiled = TreeSqlCompiler.Compile(
            new TreeQueryNode(Authors, ["id", "email", "secret_num"]) { Limit = 5 },
            CreateAccess(emailAccess: ColumnAccessLevel.Mask, secretNumAccess: ColumnAccessLevel.Mask),
            DatabaseDialect.Databricks);

        compiled.Sql.ShouldNotContain("${spark.sql.secret}");
        compiled.Sql.ShouldContain("CAST(:gq0 AS STRING)");
        compiled.Sql.ShouldContain("CAST(NULL AS STRING)");
        compiled.Parameters[":gq0"].ShouldBe("${spark.sql.secret}\\masked");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void BC_1110_03_Databricks_LikeFilter_EmitsDoubledBackslashEscape()
    {
        var tree = new TreeQueryNode(Authors, ["id"])
        {
            Limit = 5,
            Where = new TreeComparison("name", TreeFilterOperator.Contains, "a%b_c\\d")
        };

        var compiled = TreeSqlCompiler.Compile(tree, CreateAccess(), DatabaseDialect.Databricks);

        compiled.Sql.ShouldContain("LIKE :gq1 ESCAPE '\\\\'");
        compiled.Parameters[":gq1"].ShouldBe("%a\\%b\\_c\\\\d%");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task BC_1110_04_Databricks_RewritesPolicyMarkersAndRemapsSecuredSourceMarkers_FailClosed()
    {
        // 1. Subquery-free policy @ marker collides across tables -> :__rls_0 and :rfN___rls_0
        var p1 = DatabricksEqPolicy("region", "EU");
        var p2 = DatabricksEqPolicy("region", "US");
        var meta = ArticlesMeta();
        var arts2 = new TableIdentifier("lake", "main", "articles2");
        var meta2 = new TableMetadata
        {
            Identifier = arts2,
            Table = meta.Table with { TableName = "articles2" },
            PrimaryKeyColumns = ["id"],
            Columns = meta.Columns
        };
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Articles] = new(meta, AllowDecision(Articles, new Dictionary<string, ColumnAccessLevel>(), p1), "tenant_id", "acme"),
            [arts2] = new(meta2, AllowDecision(arts2, new Dictionary<string, ColumnAccessLevel>(), p2), "tenant_id", "acme")
        };
        var twoTableTree = new TreeQueryNode(Articles, ["id"])
        {
            Limit = 5,
            Relations = [new TreeRelationNode("rel2", ["id"], ["author_id"], IsList: true, new TreeQueryNode(arts2, ["id"]))]
        };

        var compiled = TreeSqlCompiler.Compile(twoTableTree, access, DatabaseDialect.Databricks);
        compiled.Sql.ShouldNotContain("@");
        compiled.Parameters.Keys.ShouldAllBe(k => k.StartsWith(':'));
        compiled.Parameters.Values.ShouldContain("EU");
        compiled.Parameters.Values.ShouldContain("US");

        TreeSqlCompiler.RenameParameters("`@x` = @x AND y = ':x' AND z = `@x`", new Dictionary<string, string> { ["x"] = ":rf1_x" })
            .ShouldBe("`@x` = :rf1_x AND y = ':x' AND z = `@x`");

        // 2. Secured source with :pN markers compiles cleanly and rejects undeclared :evil tokens
        var provider = Substitute.For<IGovernedCatalogProvider>();
        var catalog = new InMemoryTableCatalog([
            new TableCatalogEntry(new TableIdentity("main", "orders"), [new CatalogColumn("id", "INT"), new CatalogColumn("tenant_id", "STRING")], "tenant_id", 1),
            new TableCatalogEntry(new TableIdentity("main", "consents"), [new CatalogColumn("order_id", "INT"), new CatalogColumn("tenant_id", "STRING")], "tenant_id", 1)
        ], "main");
        provider.GetCatalogAsync(Arg.Any<GovernedDataSource>(), Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ITableCatalog>(catalog));
        var ordersId = new TableIdentifier("shop", "main", "orders");
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(new TableMetadata
            {
                Identifier = ci.Arg<TableIdentifier>(),
                Table = new Table { SourceName = "shop", SchemaName = "main", TableName = ci.Arg<TableIdentifier>().TableName, SourceType = "Databricks" },
                Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "order_id" }, new TableColumn { ColumnName = "tenant_id" }]
            }));

        var factory = new GovernancePolicyFactory(provider, metaRepo, Options.Create(new GatewayOptions()));
        var builder = new TreeSecuredSourceBuilder(factory, new FastSqlEngine());
        var ordersMeta = (await metaRepo.GetTableMetadataAsync(ordersId))!;
        var existsNamer = new PolicyParameterNamer("__rf_");
        var existsParam = existsNamer.Add(new PolicyValue("acme", SqlParameterType.String));
        var existsWhere = new BinaryExpression(
            new BinaryExpression(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("c"), new SqlIdentifier("order_id")])), BinaryOperator.Equal,
                PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id")),
            BinaryOperator.And,
            new BinaryExpression(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("c"), new SqlIdentifier("tenant_id")])), BinaryOperator.Equal, existsParam));
        var existsPolicy = new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(new ExistsExpression(new SelectStatement(null, new QuerySpecification(false,
                [new ColumnSelectItem(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("c"), new SqlIdentifier("order_id")])), null)],
                new NamedTableSource(new SqlQualifiedName([new SqlIdentifier("main"), new SqlIdentifier("consents")]), new SqlIdentifier("c")),
                existsWhere, null, null), null, null)), existsNamer.Values),
            TargetSqlDialect.Databricks,
            Renderer);
        var resolved = new ResolvedTableAccess(ordersMeta, AllowDecision(ordersId, new Dictionary<string, ColumnAccessLevel>(), existsPolicy), new TenantId("acme"), new Sid("S-1-5-21-1"), DefaultTenantCaller.Principal());

        var secured = await builder.BuildAsync(resolved, "shop", DatabaseDialect.Databricks, CancellationToken.None);
        secured.Dialect.ShouldBe(TargetSqlDialect.Databricks);
        Should.Throw<GatewaySecurityException>(() => TreeSecuredSourceMarkers.Remap("SELECT `id` FROM `main`.`orders` WHERE `id` = :undeclared", TargetSqlDialect.Databricks, new Dictionary<string, string>()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task BC_1110_06_GovernedTreeQueryService_AdmitsConfiguredDatabricksAndBindsTypedParameters()
    {
        var dbxOptions = DatabricksTestSupport.Options();
        var connOpts = dbxOptions.DataSources.Connections["dbx"];
        connOpts.ConnectionString.ShouldBeNullOrWhiteSpace();

        JsonElement? capturedBody = null;
        var handler = new FakeDatabricksHandler(DatabricksTestSupport.Router(req =>
        {
            if (req.Method == HttpMethod.Post && req.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                capturedBody = JsonDocument.Parse(req.Body).RootElement.Clone();
                var body = JsonSerializer.Serialize(new
                {
                    statement_id = "11111111-1111-1111-1111-111111111111",
                    status = new { state = "SUCCEEDED" },
                    manifest = new { format = "JSON_ARRAY", schema = new { column_count = 1, columns = new[] { new { name = "res", type_name = "STRING", position = 0 } } } },
                    result = new { chunk_index = 0, row_offset = 0, data_array = new[] { new[] { "[{\"id\":1}]" } } }
                });
                return DatabricksTestSupport.Ok(body);
            }

            return null;
        }));

        var tableId = new TableIdentifier("dbx", "main", "orders");
        var meta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "dbx", SchemaName = "main", TableName = "orders", SourceType = "Databricks", DataSourceType = DataSourceType.Sql, IsActive = true },
            PrimaryKeyColumns = ["id"],
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "tenant_id", DataType = "string" }]
        };
        var resolver = Substitute.For<ITableAccessResolver>();
        resolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), tableId, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(meta, TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true), new TenantId("t1"), new Sid("S-1-5-21-1"), DefaultTenantCaller.Principal()));

        var connection = new DatabricksDbConnection(DatabricksTestSupport.Client(handler, dbxOptions), connOpts.Databricks!, "dbx");
        await connection.OpenAsync();
        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(connOpts, Arg.Any<CancellationToken>()).Returns(Task.FromResult<DbConnection>(connection));

        var service = new GovernedTreeQueryService(resolver, factory, Substitute.For<IAuditLogRepository>(), Substitute.For<IColumnMaskingProvider>(), Options.Create(dbxOptions));
        using var doc = await service.ExecuteAsync(
            DefaultTenantCaller.Principal(),
            new TreeQueryNode(tableId, ["id"]) { Limit = 5, Where = new TreeComparison("id", TreeFilterOperator.Gt, 2) },
            null,
            "op-dbx-1");

        doc.RootElement.GetArrayLength().ShouldBe(1);
        capturedBody.ShouldNotBeNull();
        var paramsArray = capturedBody.Value.GetProperty("parameters").EnumerateArray().ToList();
        paramsArray.Select(p => p.GetProperty("name").GetString()!).ShouldAllBe(n => !n.StartsWith(':') && !n.StartsWith('@'));
        paramsArray.First(p => p.GetProperty("value").GetString() == "2").GetProperty("type").GetString().ShouldBe("INT");

        // Fail closed when Databricks section is missing and ConnectionString is empty
        var brokenOptions = DatabricksTestSupport.Options();
        brokenOptions.DataSources.Connections["dbx"] = new DataSourceConnectionOptions { Provider = "databricks" };
        var brokenService = new GovernedTreeQueryService(resolver, factory, Substitute.For<IAuditLogRepository>(), Substitute.For<IColumnMaskingProvider>(), Options.Create(brokenOptions));
        await Should.ThrowAsync<GatewayForbiddenException>(() => brokenService.ExecuteAsync(DefaultTenantCaller.Principal(), new TreeQueryNode(tableId, ["id"]) { Limit = 5 }, null, "op-dbx-2"));
    }
}
