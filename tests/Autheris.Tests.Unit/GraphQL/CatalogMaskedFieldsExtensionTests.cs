using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// F-GOV-16: the catalog-based GraphQL schema reports the response path of every selected column that is masked for
/// the caller in <c>extensions.maskedFields</c>; clear columns produce no entry, requests do not share entries.
/// </summary>
public sealed class CatalogMaskedFieldsExtensionTests
{
    private static readonly TableIdentifier Customers = new("sales", "dbo", "customers");
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private const string TwoCustomersJson = """
        [
            { "id": 1, "email": "***", "orders": [ { "order_id": 101, "note": "***" }, { "order_id": 102, "note": "***" } ] },
            { "id": 2, "email": "***", "orders": [ { "order_id": 201, "note": "***" } ] }
        ]
        """;

    private static async Task<IRequestExecutor> CreateExecutorAsync(IGovernedTreeQueryService treeService, string user)
    {
        var customers = new TableMetadata
        {
            Identifier = Customers,
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "email", DataType = "nvarchar(100)" }
            ],
            PrimaryKeyColumns = ["id"]
        };
        var orders = new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns =
            [
                new TableColumn { ColumnName = "order_id", DataType = "int" },
                new TableColumn { ColumnName = "customer_id", DataType = "int" },
                new TableColumn { ColumnName = "note", DataType = "nvarchar(100)" }
            ],
            PrimaryKeyColumns = ["order_id"]
        };

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([customers, orders]);

        var relationRepo = Substitute.For<ITableRelationRepository>();
        relationRepo.GetRelationsForTableAsync(Customers, Arg.Any<CancellationToken>()).Returns(
        [
            new TableRelation
            {
                RelationName = "orders",
                ParentTableIdentifier = Customers,
                ChildTableIdentifier = Orders,
                Cardinality = RelationCardinality.OneToMany,
                JoinKeysParent = ["id"],
                JoinKeysChild = ["customer_id"]
            }
        ]);
        relationRepo.GetRelationsForTableAsync(Orders, Arg.Any<CancellationToken>()).Returns([]);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user), new Claim("tenant_id", "T1")], "TestAuth"))
        };

        var services = new ServiceCollection();
        services.AddSingleton(metadataRepo);
        services.AddSingleton(relationRepo);
        services.AddSingleton(treeService);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton(Options.Create(new GatewayOptions()));

        return await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .UseRequest<CatalogOperationCleanupMiddleware>()
            .UseRequest<MaskedFieldsMiddleware>()
            .UseDefaultPipeline()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<ITableRelationRepository>()))
            .BuildRequestExecutorAsync();
    }

    private static IGovernedTreeQueryService TreeService(IReadOnlyList<TreeMaskedColumn> masked)
    {
        var service = Substitute.For<IGovernedTreeQueryService>();
        service.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse(TwoCustomersJson));
        service.GetMaskedColumns(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TreeQueryNode>(), Arg.Any<string>())
            .Returns(masked);
        return service;
    }

    private static IReadOnlyList<string>? MaskedFields(IExecutionResult result)
    {
        var op = result.ExpectOperationResult();
        return op.Extensions is not null && op.Extensions.TryGetValue("maskedFields", out var value)
            ? value as IReadOnlyList<string>
            : null;
    }

    [Fact]
    public async Task MaskedColumn_ReportsPathOfEveryRow()
    {
        var executor = await CreateExecutorAsync(TreeService([new TreeMaskedColumn([], "email")]), "alice");

        var result = await executor.ExecuteAsync("{ sales_dbo_customers { id email } }");

        var masked = MaskedFields(result);
        masked.ShouldNotBeNull();
        masked.ShouldBe(["/sales_dbo_customers/0/email", "/sales_dbo_customers/1/email"], ignoreOrder: true);
    }

    [Fact]
    public async Task OnlyClearColumnsSelected_HasNoMaskedFieldsExtension()
    {
        var executor = await CreateExecutorAsync(TreeService([new TreeMaskedColumn([], "email")]), "alice");

        var result = await executor.ExecuteAsync("{ sales_dbo_customers { id } }");

        MaskedFields(result).ShouldBeNull();
    }

    [Fact]
    public async Task NestedMaskedColumn_ReportsNestedPaths_AndSkipsClearSiblingsAndSameNamedRootColumns()
    {
        var executor = await CreateExecutorAsync(TreeService([new TreeMaskedColumn(["orders"], "note")]), "alice");

        var result = await executor.ExecuteAsync("{ sales_dbo_customers { id email orders { order_id note } } }");

        var masked = MaskedFields(result);
        masked.ShouldNotBeNull();
        masked.ShouldBe(
            ["/sales_dbo_customers/0/orders/0/note", "/sales_dbo_customers/0/orders/1/note", "/sales_dbo_customers/1/orders/0/note"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task AliasedRelation_UsesTheAliasPath()
    {
        var executor = await CreateExecutorAsync(TreeService([new TreeMaskedColumn(["o"], "note")]), "alice");

        var result = await executor.ExecuteAsync("{ sales_dbo_customers { id o: orders { note } } }");

        var masked = MaskedFields(result);
        masked.ShouldNotBeNull();
        masked.ShouldBe(
            ["/sales_dbo_customers/0/o/0/note", "/sales_dbo_customers/0/o/1/note", "/sales_dbo_customers/1/o/0/note"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task ConcurrentRequestsOfDifferentUsers_DoNotShareEntries()
    {
        var maskedExecutor = await CreateExecutorAsync(TreeService([new TreeMaskedColumn([], "email")]), "alice");
        var clearExecutor = await CreateExecutorAsync(TreeService([]), "bob");

        var tasks = Enumerable.Range(0, 20).Select(i => i % 2 == 0
            ? maskedExecutor.ExecuteAsync("{ sales_dbo_customers { id email } }")
            : clearExecutor.ExecuteAsync("{ sales_dbo_customers { id email } }"));
        var results = await Task.WhenAll(tasks);

        for (var i = 0; i < results.Length; i++)
        {
            var masked = MaskedFields(results[i]);
            if (i % 2 == 0)
            {
                masked.ShouldNotBeNull();
                masked.Count.ShouldBe(2);
            }
            else
            {
                masked.ShouldBeNull();
            }
        }
    }

    [Fact]
    public void CollectMaskedColumns_ListsOnlyMaskedSelectedColumnsWithRelationPath()
    {
        var customers = new TableMetadata
        {
            Identifier = Customers,
            Table = new Table { SourceName = "sales", SchemaName = "dbo", TableName = "customers", SourceType = "Sqlite" },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "email", DataType = "text" }
            ]
        };
        var orders = new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { SourceName = "sales", SchemaName = "dbo", TableName = "orders", SourceType = "Sqlite" },
            PrimaryKeyColumns = ["order_id"],
            Columns =
            [
                new TableColumn { ColumnName = "order_id", DataType = "integer" },
                new TableColumn { ColumnName = "customer_id", DataType = "integer" },
                new TableColumn { ColumnName = "note", DataType = "text" }
            ]
        };
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Customers] = new(customers, TableAccessDecision.Allowed(Customers,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["email"] = ColumnAccessLevel.Mask },
                null, hasUnconstrainedColumnAllow: true, null)),
            [Orders] = new(orders, TableAccessDecision.Allowed(Orders,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["note"] = ColumnAccessLevel.Mask },
                null, hasUnconstrainedColumnAllow: true, null))
        };
        var root = new TreeQueryNode(Customers, ["id", "email"])
        {
            Relations = [new TreeRelationNode("o", ["id"], ["customer_id"], true, new TreeQueryNode(Orders, ["order_id", "note"]))]
        };

        var masked = TreeSqlCompiler.CollectMaskedColumns(root, access);

        masked.Count.ShouldBe(2);
        masked.ShouldContain(m => m.Column == "email" && m.Path.Count == 0);
        masked.ShouldContain(m => m.Column == "note" && m.Path.SequenceEqual(new[] { "o" }));
        TreeSqlCompiler.CollectMaskedColumns(root, access, maskingDisabled: true).ShouldBeEmpty();
    }
}
