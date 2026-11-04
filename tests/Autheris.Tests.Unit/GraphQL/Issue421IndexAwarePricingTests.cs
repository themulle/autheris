namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #421 (WP-D of #417): GraphQL cost model: index-aware pricing for root-list plans.
/// </summary>
public sealed class Issue421IndexAwarePricingTests
{
    public sealed class OrderItem
    {
        public int Id { get; set; } = 1;
        public string Name { get; set; } = "Item";
    }

    public sealed class OrderSortInput
    {
        public string? Id { get; set; }
        public string? UnindexedCol { get; set; }
    }

    [GraphQLName("Query")]
    public sealed class OrdersQuery
    {
        public List<OrderItem> GetOrders(int first = 10, List<OrderSortInput>? orderBy = null) => [new()];
    }

    private static IHostEnvironment Env(string name = "Production")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static async Task<ISchemaDefinition> SchemaAsync()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer().AddQueryType<OrdersQuery>();
        var provider = services.BuildServiceProvider().GetRequiredService<IRequestExecutorProvider>();
        return (await provider.GetExecutorAsync()).Schema;
    }

    [Fact]
    public void QueryCostOptions_DefaultUnindexedMultiplier_IsTwo()
    {
        var options = new QueryCostOptions();
        options.UnindexedRootListSurchargeMultiplier.ShouldBe(2.0);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.5)]
    [InlineData(50.0)]
    public void StartupValidator_ValidUnindexedMultiplier_PassesValidation(double multiplier)
    {
        var options = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = multiplier };
        var act = () => GatewayStartupValidator.ValidateQueryCostOptions(options, Env());
        act.ShouldNotThrow();
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(50.1)]
    [InlineData(100.0)]
    [InlineData(double.NaN)]
    public void StartupValidator_OutOfRangeUnindexedMultiplier_ThrowsValidationException(double multiplier)
    {
        var options = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = multiplier };
        var act = () => GatewayStartupValidator.ValidateQueryCostOptions(options, Env());
        var ex = Should.Throw<ValidationException>(act);
        ex.Message.ShouldContain("UnindexedRootListSurchargeMultiplier must be 1.0..50.0");
    }

    [Fact]
    public void HasUsableIndex_PrimaryKeyAscending_MatchesAscendingOrderBy()
    {
        var metadata = CreateTable(
            pk: ["id"],
            pkDirections: [KeyColumnDirection.Ascending]);

        var orderBy = new List<(string, string)> { ("id", "ASC") };
        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy, filterColumns: null);

        isUsable.ShouldBeTrue();
    }

    [Fact]
    public void HasUsableIndex_PrimaryKeyAscending_MismatchesDescendingOrderBy()
    {
        var metadata = CreateTable(
            pk: ["id"],
            pkDirections: [KeyColumnDirection.Ascending]);

        var orderBy = new List<(string, string)> { ("id", "DESC") };
        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy, filterColumns: null);

        // Direction mismatch forces full source sort -> unindexed plan
        isUsable.ShouldBeFalse();
    }

    [Fact]
    public void HasUsableIndex_PrimaryKeyDescending_MatchesDescendingOrderBy()
    {
        var metadata = CreateTable(
            pk: ["id"],
            pkDirections: [KeyColumnDirection.Descending]);

        var orderBy = new List<(string, string)> { ("id", "DESC") };
        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy, filterColumns: null);

        isUsable.ShouldBeTrue();
    }

    [Fact]
    public void HasUsableIndex_UnindexedSortColumn_ReturnsFalse()
    {
        var metadata = CreateTable(pk: ["id"]);

        var orderBy = new List<(string, string)> { ("description", "ASC") };
        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy, filterColumns: null);

        isUsable.ShouldBeFalse();
    }

    [Fact]
    public void HasUsableIndex_SecondaryUniqueIndex_MatchesOrderByPrefix()
    {
        var metadata = CreateTable(
            pk: ["id"],
            indexes: [new TableIndexDefinition("idx_tenant_created", ["tenant_id", "created_at"], IsUnique: true)]);

        var orderBy = new List<(string, string)> { ("tenant_id", "ASC"), ("created_at", "ASC") };
        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy, filterColumns: null);

        isUsable.ShouldBeTrue();
    }

    [Fact]
    public void HasUsableIndex_PhysicalNonUniqueIndex_MatchesFilter()
    {
        var metadata = CreateTable(
            pk: ["id"],
            physicalIndexes: [new PhysicalIndexDefinition(["customer_id"], [])]);

        var filter = new List<string> { "customer_id" };
        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy: null, filterColumns: filter);

        isUsable.ShouldBeTrue();
    }

    [Fact]
    public void HasUsableIndex_UnindexedFilterAndNoOrder_ReturnsFalse()
    {
        var metadata = CreateTable(pk: ["id"]);

        var filter = new List<string> { "customer_notes" };
        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy: null, filterColumns: filter);

        isUsable.ShouldBeFalse();
    }

    [Fact]
    public void HasUsableIndex_NoOrderAndNoFilter_TableWithPk_ReturnsTrue()
    {
        var metadata = CreateTable(pk: ["id"]);

        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy: null, filterColumns: null);

        // Default PK ordering seek
        isUsable.ShouldBeTrue();
    }

    [Fact]
    public void HasUsableIndex_NoOrderAndNoFilter_ReturnsTrue()
    {
        var metadata = CreateTable(pk: []);

        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy: null, filterColumns: null);

        // Top-K sequential read does not perform an unindexed sort or filter
        isUsable.ShouldBeTrue();
    }

    [Fact]
    public void HasUsableIndex_IndexColumnPrunedByVisibility_ReturnsFalse()
    {
        var metadata = CreateTable(
            pk: ["id"],
            indexes: [new TableIndexDefinition("idx_secret", ["secret_code"], IsUnique: false)]);

        var orderBy = new List<(string, string)> { ("secret_code", "ASC") };

        // Caller only has visibility to "id" and "name" (secret_code is pruned)
        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(
            metadata,
            visibleColumns: ["id", "name"],
            orderBy: orderBy,
            filterColumns: null);

        // Pruned index is not usable for this caller
        isUsable.ShouldBeFalse();
    }

    [Fact]
    public async Task CalculateCost_UnindexedRootList_AppliesSurchargeMultiplier()
    {
        var metadata = CreateTable(pk: ["id"]);
        var costOptions = new QueryCostOptions
        {
            DefaultListMultiplier = 10,
            UnindexedRootListSurchargeMultiplier = 3.0,
            ScalarColumnWeight = 1.0
        };

        // Query 1: Indexed ordering by id
        var indexedDoc = Utf8GraphQLParser.Parse("{ orders(first: 10, orderBy: [{ id: \"ASC\" }]) { id name } }");
        // Query 2: Unindexed ordering by unindexedCol
        var unindexedDoc = Utf8GraphQLParser.Parse("{ orders(first: 10, orderBy: [{ unindexedCol: \"ASC\" }]) { id name } }");

        var schema = await SchemaAsync();

        var indexedCost = QueryCostAnalyzerRule.CalculateCost(
            indexedDoc,
            schema,
            defaultListMultiplier: 10,
            costOptions: costOptions,
            tableMetadataLookup: name => metadata);

        var unindexedCost = QueryCostAnalyzerRule.CalculateCost(
            unindexedDoc,
            schema,
            defaultListMultiplier: 10,
            costOptions: costOptions,
            tableMetadataLookup: name => metadata);

        // Unindexed query must be strictly priced higher
        unindexedCost.ShouldBeGreaterThan(indexedCost);

        // Indexed cost:
        // rows = 10, multiplier = 10, base = 100. Child = 2 scalars * 10 = 20. Total = 120.
        // Unindexed cost:
        // rows = 10, multiplier = ceil(10 * 3.0) = 30, base = 300. Child = 20. Total = 320.
        indexedCost.ShouldBe(120);
        unindexedCost.ShouldBe(320);
    }

    [Fact]
    public async Task CalculateCost_SurchargeMultiplierOne_YieldsIdenticalPrices()
    {
        var metadata = CreateTable(pk: ["id"]);
        var costOptions = new QueryCostOptions
        {
            DefaultListMultiplier = 10,
            UnindexedRootListSurchargeMultiplier = 1.0,
            ScalarColumnWeight = 1.0
        };

        var indexedDoc = Utf8GraphQLParser.Parse("{ orders(first: 10, orderBy: [{ id: \"ASC\" }]) { id name } }");
        var unindexedDoc = Utf8GraphQLParser.Parse("{ orders(first: 10, orderBy: [{ unindexedCol: \"ASC\" }]) { id name } }");

        var schema = await SchemaAsync();

        var indexedCost = QueryCostAnalyzerRule.CalculateCost(
            indexedDoc,
            schema,
            defaultListMultiplier: 10,
            costOptions: costOptions,
            tableMetadataLookup: name => metadata);

        var unindexedCost = QueryCostAnalyzerRule.CalculateCost(
            unindexedDoc,
            schema,
            defaultListMultiplier: 10,
            costOptions: costOptions,
            tableMetadataLookup: name => metadata);

        unindexedCost.ShouldBe(indexedCost);
    }

    [Fact]
    public void HasUsableIndex_IndexedOrderByWithUnindexedWhere_ReturnsFalse()
    {
        // HIGH-03: Indexed orderBy must not mask an unindexed where filter
        var metadata = CreateTable(
            pk: ["id"],
            pkDirections: [KeyColumnDirection.Ascending]);

        var orderBy = new List<(string, string)> { ("id", "ASC") };
        var filter = new List<string> { "unindexedCol" };

        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(
            metadata,
            visibleColumns: null,
            orderBy: orderBy,
            filterColumns: filter);

        isUsable.ShouldBeFalse();
    }

    [Fact]
    public void HasUsableIndex_IndexedOrderByWithIndexedWhere_ReturnsTrue()
    {
        var metadata = CreateTable(
            pk: ["id"],
            pkDirections: [KeyColumnDirection.Ascending],
            physicalIndexes: [new PhysicalIndexDefinition(["customer_id"], [])]);

        var orderBy = new List<(string, string)> { ("id", "ASC") };
        var filter = new List<string> { "customer_id" };

        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(
            metadata,
            visibleColumns: null,
            orderBy: orderBy,
            filterColumns: filter);

        isUsable.ShouldBeTrue();
    }

    [Fact]
    public void HasUsableIndex_UnresolvedVariableMarker_ReturnsFalse()
    {
        var metadata = CreateTable(pk: ["id"]);

        var filter = new List<string> { IndexUsabilityEvaluator.UnresolvedVariableMarker };
        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy: null, filterColumns: filter);
        isUsable.ShouldBeFalse();

        var orderBy = new List<(string, string)> { (IndexUsabilityEvaluator.UnresolvedVariableMarker, "ASC") };
        var isUsableOrder = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy: orderBy, filterColumns: null);
        isUsableOrder.ShouldBeFalse();
    }

    [Fact]
    public void HasUsableIndex_OrDisjunction_OneBranchUnindexed_ReturnsFalse()
    {
        var metadata = CreateTable(
            pk: ["id"],
            indexes: [new TableIndexDefinition("idx_tenant", ["tenant_id"], IsUnique: false)]);

        // Branch 0 (id) is indexed; Branch 1 (unindexedCol) is not indexed
        var filter = new List<string>
        {
            $"{IndexUsabilityEvaluator.OrBranchPrefix}0:id",
            $"{IndexUsabilityEvaluator.OrBranchPrefix}1:unindexedCol"
        };

        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy: null, filterColumns: filter);
        isUsable.ShouldBeFalse();
    }

    [Fact]
    public void HasUsableIndex_OrDisjunction_AllBranchesIndexed_ReturnsTrue()
    {
        var metadata = CreateTable(
            pk: ["id"],
            indexes: [new TableIndexDefinition("idx_tenant", ["tenant_id"], IsUnique: false)]);

        // Both Branch 0 (id) and Branch 1 (tenant_id) are index-backed
        var filter = new List<string>
        {
            $"{IndexUsabilityEvaluator.OrBranchPrefix}0:id",
            $"{IndexUsabilityEvaluator.OrBranchPrefix}1:tenant_id"
        };

        var isUsable = IndexUsabilityEvaluator.HasUsableIndex(metadata, visibleColumns: null, orderBy: null, filterColumns: filter);
        isUsable.ShouldBeTrue();
    }

    [Fact]
    public void ExtractFilterColumns_ScanOperator_DoesNotCreditColumn()
    {
        // where: { name: { contains: "foo" } }
        var arg = new ArgumentNode("where", new ObjectValueNode(
            new ObjectFieldNode("name", new ObjectValueNode(
                new ObjectFieldNode("contains", new StringValueNode("foo"))))));

        var cols = IndexUsabilityEvaluator.ExtractFilterColumns(arg);
        cols.ShouldNotBeNull();
        cols.ShouldContain(IndexUsabilityEvaluator.UnusableFilterMarker);
    }

    [Fact]
    public void ExtractFilterColumns_SeekableOperator_CreditsColumn()
    {
        // where: { id: { eq: 1 } }
        var arg = new ArgumentNode("where", new ObjectValueNode(
            new ObjectFieldNode("id", new ObjectValueNode(
                new ObjectFieldNode("eq", new IntValueNode(1))))));

        var cols = IndexUsabilityEvaluator.ExtractFilterColumns(arg);
        cols.ShouldNotBeNull();
        cols.ShouldContain("id");
    }

    [Fact]
    public void ExtractFilterColumns_NotOperator_DoesNotCreditInnerColumn()
    {
        // where: { not: { id: { eq: 1 } } }
        var arg = new ArgumentNode("where", new ObjectValueNode(
            new ObjectFieldNode("not", new ObjectValueNode(
                new ObjectFieldNode("id", new ObjectValueNode(
                    new ObjectFieldNode("eq", new IntValueNode(1))))))));

        var cols = IndexUsabilityEvaluator.ExtractFilterColumns(arg);
        cols.ShouldNotBeNull();
        cols.ShouldContain(IndexUsabilityEvaluator.UnusableFilterMarker);
    }

    [Fact]
    public void ExtractOrderBy_UnresolvedVariable_ReturnsMarker()
    {
        var arg = new ArgumentNode("orderBy", new VariableNode("orderVar"));
        var orders = IndexUsabilityEvaluator.ExtractOrderBy(arg, variableValues: null);
        orders.ShouldNotBeNull();
        orders.Count.ShouldBe(1);
        orders[0].FieldName.ShouldBe(IndexUsabilityEvaluator.UnresolvedVariableMarker);
    }

    [Fact]
    public void ExtractOrderBy_ResolvedVariable_ReturnsOrders()
    {
        var arg = new ArgumentNode("orderBy", new VariableNode("orderVar"));
        var vars = new Dictionary<string, object?>
        {
            ["orderVar"] = new List<object> { new Dictionary<string, object?> { ["id"] = "DESC" } }
        };
        var orders = IndexUsabilityEvaluator.ExtractOrderBy(arg, variableValues: vars);
        orders.ShouldNotBeNull();
        orders.Count.ShouldBe(1);
        orders[0].FieldName.ShouldBe("id");
        orders[0].Direction.ShouldBe("DESC");
    }

    private static TableMetadata CreateTable(
        IReadOnlyList<string>? pk = null,
        IReadOnlyList<KeyColumnDirection>? pkDirections = null,
        IReadOnlyList<TableIndexDefinition>? indexes = null,
        IReadOnlyList<PhysicalIndexDefinition>? physicalIndexes = null)
    {
        var cols = new List<TableColumn>
        {
            new() { ColumnName = "id", DataType = "int" },
            new() { ColumnName = "name", DataType = "varchar" },
            new() { ColumnName = "tenant_id", DataType = "varchar" },
            new() { ColumnName = "created_at", DataType = "datetime" },
            new() { ColumnName = "customer_id", DataType = "int" },
            new() { ColumnName = "description", DataType = "varchar" },
            new() { ColumnName = "secret_code", DataType = "varchar" },
            new() { ColumnName = "unindexedCol", DataType = "varchar" }
        };

        return new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "orders"),
            Columns = cols,
            PrimaryKeyColumns = pk ?? [],
            PrimaryKeyDirections = pkDirections ?? [],
            Indexes = indexes ?? [],
            PhysicalIndexes = physicalIndexes ?? []
        };
    }
}
