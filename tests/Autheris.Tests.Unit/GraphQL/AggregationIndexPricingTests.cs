#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Common.Quota;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Application.State.Caching.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Collection("CatalogRelation")]
public sealed class AggregationIndexPricingTests
{
    private const string Sdl = """
        type Query {
          orders(first: Int = 10, where: OrderFilter, orderBy: [OrderSort!]): [Order!]!
          ordersConnection(first: Int = 10, where: OrderFilter, orderBy: [OrderSort!]): OrderConnection!
          orders_aggregate(
            groupBy: [orders_column!],
            timeBuckets: [TimeBucketInput!],
            where: OrderFilter,
            aggregates: [orders_aggregate_input!]!,
            limit: Int = 100
          ): orders_aggregate_result!
          queryMetrics(metrics: [String!]!, groupBy: [String!], limit: Int): MetricResult!
        }
        enum orders_column { id customer_id region secret_col created_at }
        enum Fn { COUNT COUNT_DISTINCT SUM }
        enum BucketInterval { HOUR DAY }
        input TimeBucketInput { column: orders_column! bucket: BucketInterval! }
        input orders_aggregate_input { fn: Fn! column: orders_column alias: String! }
        input IntFilter { eq: Int neq: Int gt: Int }
        input StringFilter { eq: String contains: String }
        input OrderFilter {
          id: IntFilter
          customer_id: IntFilter
          region: StringFilter
          secret_col: StringFilter
          and: [OrderFilter!]
        }
        input OrderSort {
          id: String
          customer_id: String
          region: String
          secret_col: String
        }
        type orders_aggregate_result { groups: [Group!]! count: Long! }
        type Group { key: Key! rowCount: Long! aggregates: [Value!]! }
        type Key { region: String customer_id: Int }
        type Value { alias: String! kind: String! intValue: Long }
        type MetricResult { metrics: [String!]! }
        type OrderConnection { nodes: [Order!]! }
        type Order { id: Int! customer_id: Int! region: String! secret_col: String }
        scalar Long
        """;

    private static async Task<ISchemaDefinition> SchemaAsync()
    {
        var builder = new ServiceCollection().AddGraphQLServer().AddDocumentFromString(Sdl);
        foreach (var type in Utf8GraphQLParser.Parse(Sdl).Definitions.OfType<ObjectTypeDefinitionNode>())
        {
            foreach (var field in type.Fields)
            {
                builder = builder.AddResolver(type.Name.Value, field.Name.Value, _ => (object?)null);
            }
        }

        return await builder.BuildSchemaAsync();
    }

    private static TableMetadata CreateOrdersMetadata(bool indexCustomerId = true, bool indexCreatedAt = false) => new()
    {
        Identifier = new TableIdentifier("crm", "public", "orders"),
        Table = new Table
        {
            TableName = "orders",
            SchemaName = "public",
            SourceName = "crm",
            SourceType = "PostgreSQL",
            IsActive = true,
            DataSourceType = DataSourceType.Sql
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "customer_id", DataType = "int" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "secret_col", DataType = "varchar" },
            new TableColumn { ColumnName = "created_at", DataType = "timestamp" }
        ],
        PrimaryKeyColumns = ["id"],
        Indexes =
        [
            .. (indexCustomerId ? new[] { new TableIndexDefinition("idx_orders_customer", ["customer_id"], false, null) } : []),
            .. (indexCreatedAt ? new[] { new TableIndexDefinition("idx_orders_created_at", ["created_at"], false, null) } : [])
        ]
    };

    [Fact]
    [Trait("Category", "Security")]
    public async Task Aggregation_without_usable_index_gets_surcharge()
    {
        var schema = await SchemaAsync();
        var ordersMeta = CreateOrdersMetadata(indexCustomerId: false);
        var costOptions = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 3.0 };

        // 1. Unfiltered, ungrouped T_aggregate scans the whole table -> surcharged + hint with []
        var scanDoc = Utf8GraphQLParser.Parse("{ orders_aggregate(aggregates: [{ fn: COUNT, alias: \"c\" }]) { count } }");
        int baselineScan = QueryCostAnalyzerRule.CalculateCost(scanDoc, schema, costOptions: costOptions);

        var scanHints = new List<GraphQlIndexHint>();
        int surchargedScan = QueryCostAnalyzerRule.CalculateCost(
            scanDoc,
            schema,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: scanHints);

        surchargedScan.ShouldBe((int)Math.Min(int.MaxValue, Math.Ceiling(baselineScan * 3.0)));
        scanHints.ShouldHaveSingleItem();
        scanHints[0].Root.ShouldBe("orders_aggregate");
        scanHints[0].Reason.ShouldBe(GraphQlIndexHint.NoUsableIndexReason);
        scanHints[0].HelpfulIndexColumns.ShouldBeEmpty();

        // 2. Grouped by unindexed column -> surcharged + helpfulIndexColumns = ["region"]
        var groupDoc = Utf8GraphQLParser.Parse("{ byRegion: orders_aggregate(groupBy: [region], aggregates: [{ fn: COUNT, alias: \"c\" }]) { groups { rowCount } } }");
        int baselineGroup = QueryCostAnalyzerRule.CalculateCost(groupDoc, schema, costOptions: costOptions);

        var groupHints = new List<GraphQlIndexHint>();
        int surchargedGroup = QueryCostAnalyzerRule.CalculateCost(
            groupDoc,
            schema,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: groupHints);

        surchargedGroup.ShouldBe((int)Math.Min(int.MaxValue, Math.Ceiling(baselineGroup * 3.0)));
        groupHints.ShouldHaveSingleItem();
        groupHints[0].Root.ShouldBe("byRegion");
        groupHints[0].Reason.ShouldBe(GraphQlIndexHint.NoUsableIndexReason);
        groupHints[0].HelpfulIndexColumns.ShouldBe(["region"]);

        // 3. Large multiplier saturates cleanly at int.MaxValue without overflow wrap
        var extremeCostOptions = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 50.0 };
        var saturatedHints = new List<GraphQlIndexHint>();
        int saturated = QueryCostAnalyzerRule.CalculateCost(
            groupDoc,
            schema,
            costOptions: extremeCostOptions,
            aggregation: new AggregationOptions { AggregateFieldCostWeight = int.MaxValue / 2.0 },
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: saturatedHints);

        saturated.ShouldBe(int.MaxValue);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Aggregation_with_leading_key_filter_not_surcharged()
    {
        var schema = await SchemaAsync();
        var ordersMeta = CreateOrdersMetadata(indexCustomerId: true, indexCreatedAt: true);
        var costOptions = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 3.0 };

        // Leading key filter on indexed customer_id -> NOT surcharged
        var filteredDoc = Utf8GraphQLParser.Parse("{ orders_aggregate(where: { customer_id: { eq: 42 } }, aggregates: [{ fn: COUNT, alias: \"c\" }]) { count } }");
        int baseFilter = QueryCostAnalyzerRule.CalculateCost(filteredDoc, schema, costOptions: costOptions);
        var filterHints = new List<GraphQlIndexHint>();
        int actualFilter = QueryCostAnalyzerRule.CalculateCost(
            filteredDoc,
            schema,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: filterHints);

        actualFilter.ShouldBe(baseFilter);
        filterHints.ShouldBeEmpty();

        // Grouped by indexed column -> NOT surcharged
        var groupedDoc = Utf8GraphQLParser.Parse("{ orders_aggregate(groupBy: [customer_id], aggregates: [{ fn: COUNT, alias: \"c\" }]) { groups { rowCount } } }");
        int baseGroup = QueryCostAnalyzerRule.CalculateCost(groupedDoc, schema, costOptions: costOptions);
        var groupHints = new List<GraphQlIndexHint>();
        int actualGroup = QueryCostAnalyzerRule.CalculateCost(
            groupedDoc,
            schema,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: groupHints);

        actualGroup.ShouldBe(baseGroup);
        groupHints.ShouldBeEmpty();

        // Time-bucketed by indexed created_at -> NOT surcharged
        var bucketDoc = Utf8GraphQLParser.Parse("{ orders_aggregate(timeBuckets: [{ column: created_at, bucket: DAY }], aggregates: [{ fn: COUNT, alias: \"c\" }]) { groups { rowCount } } }");
        int baseBucket = QueryCostAnalyzerRule.CalculateCost(bucketDoc, schema, costOptions: costOptions);
        var bucketHints = new List<GraphQlIndexHint>();
        int actualBucket = QueryCostAnalyzerRule.CalculateCost(
            bucketDoc,
            schema,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: bucketHints);

        actualBucket.ShouldBe(baseBucket);
        bucketHints.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Aggregation_with_unresolved_variable_in_where_or_groupBy_fails_closed_with_surcharge()
    {
        var schema = await SchemaAsync();
        var ordersMeta = CreateOrdersMetadata(indexCustomerId: true);
        var costOptions = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 2.5 };

        // 1. Unresolved $w in where
        var whereVarDoc = Utf8GraphQLParser.Parse("query($w: OrderFilter) { orders_aggregate(where: $w, aggregates: [{ fn: COUNT, alias: \"c\" }]) { count } }");
        int whereBaseline = QueryCostAnalyzerRule.CalculateCost(whereVarDoc, schema, costOptions: costOptions);
        var whereHints = new List<GraphQlIndexHint>();
        int whereActual = QueryCostAnalyzerRule.CalculateCost(
            whereVarDoc,
            schema,
            variableValues: null,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: whereHints);

        whereActual.ShouldBe((int)Math.Ceiling(whereBaseline * 2.5));
        whereHints.ShouldHaveSingleItem();
        whereHints[0].HelpfulIndexColumns.ShouldBeEmpty();

        // 2. Unresolved $g in groupBy
        var groupVarDoc = Utf8GraphQLParser.Parse("query($g: [orders_column!]) { orders_aggregate(groupBy: $g, aggregates: [{ fn: COUNT, alias: \"c\" }]) { count } }");
        int groupBaseline = QueryCostAnalyzerRule.CalculateCost(groupVarDoc, schema, costOptions: costOptions);
        var groupHints = new List<GraphQlIndexHint>();
        int groupActual = QueryCostAnalyzerRule.CalculateCost(
            groupVarDoc,
            schema,
            variableValues: null,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: groupHints);

        groupActual.ShouldBe((int)Math.Ceiling(groupBaseline * 2.5));
        groupHints.ShouldHaveSingleItem();
        groupHints[0].HelpfulIndexColumns.ShouldBeEmpty();

        // 3. Unresolved $tb in timeBuckets
        var tbVarDoc = Utf8GraphQLParser.Parse("query($tb: [TimeBucketInput!]) { orders_aggregate(timeBuckets: $tb, aggregates: [{ fn: COUNT, alias: \"c\" }]) { count } }");
        int tbBaseline = QueryCostAnalyzerRule.CalculateCost(tbVarDoc, schema, costOptions: costOptions);
        var tbHints = new List<GraphQlIndexHint>();
        int tbActual = QueryCostAnalyzerRule.CalculateCost(
            tbVarDoc,
            schema,
            variableValues: null,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: tbHints);

        tbActual.ShouldBe((int)Math.Ceiling(tbBaseline * 2.5));
        tbHints.ShouldHaveSingleItem();
        tbHints[0].HelpfulIndexColumns.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Index_hint_omits_columns_when_any_is_denied()
    {
        var schema = await SchemaAsync();
        var ordersMeta = CreateOrdersMetadata(indexCustomerId: false);
        var costOptions = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 2.0 };
        IReadOnlySet<string> visibleOnlyNonSecret = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "customer_id", "region" };

        // 1. T_aggregate mixes visible "region" and denied "secret_col" -> all-or-nothing empty HelpfulIndexColumns
        var aggDoc = Utf8GraphQLParser.Parse("{ orders_aggregate(where: { region: { eq: \"EMEA\" }, secret_col: { eq: \"x\" } }, groupBy: [region], aggregates: [{ fn: COUNT, alias: \"c\" }]) { count } }");
        var aggHints = new List<GraphQlIndexHint>();
        QueryCostAnalyzerRule.CalculateCost(
            aggDoc,
            schema,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            visibleColumnsLookup: _ => visibleOnlyNonSecret,
            indexHintsOut: aggHints);

        aggHints.ShouldHaveSingleItem();
        aggHints[0].Reason.ShouldBe(GraphQlIndexHint.NoUsableIndexReason);
        aggHints[0].HelpfulIndexColumns.ShouldBeEmpty();

        // 2. Root list mixes visible "region" and denied "secret_col" in orderBy -> all-or-nothing empty HelpfulIndexColumns
        var listDoc = Utf8GraphQLParser.Parse("{ orders(first: 10, where: { region: { eq: \"EMEA\" } }, orderBy: [{ secret_col: \"ASC\" }]) { id } }");
        var listHints = new List<GraphQlIndexHint>();
        QueryCostAnalyzerRule.CalculateCost(
            listDoc,
            schema,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            visibleColumnsLookup: _ => visibleOnlyNonSecret,
            indexHintsOut: listHints);

        listHints.ShouldHaveSingleItem();
        listHints[0].Reason.ShouldBe(GraphQlIndexHint.NoUsableIndexReason);
        listHints[0].HelpfulIndexColumns.ShouldBeEmpty();

        // 3. Root list with only visible columns -> distinct filter then order columns
        var visibleListDoc = Utf8GraphQLParser.Parse("{ orders(first: 10, where: { region: { eq: \"EMEA\" } }, orderBy: [{ region: \"ASC\" }, { customer_id: \"DESC\" }]) { id } }");
        var visibleHints = new List<GraphQlIndexHint>();
        QueryCostAnalyzerRule.CalculateCost(
            visibleListDoc,
            schema,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            visibleColumnsLookup: _ => visibleOnlyNonSecret,
            indexHintsOut: visibleHints);

        visibleHints.ShouldHaveSingleItem();
        visibleHints[0].HelpfulIndexColumns.ShouldBe(["region", "customer_id"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Hint_absent_when_index_usable()
    {
        var schema = await SchemaAsync();
        var ordersMeta = CreateOrdersMetadata(indexCustomerId: true);
        var costOptions = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 2.0 };

        var indexedListDoc = Utf8GraphQLParser.Parse("{ orders(first: 10, orderBy: [{ id: \"ASC\" }]) { id } }");
        var listHints = new List<GraphQlIndexHint>();
        QueryCostAnalyzerRule.CalculateCost(
            indexedListDoc,
            schema,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: listHints);

        listHints.ShouldBeEmpty();

        // queryMetrics synthetic root field is never surcharged or hinted
        var metricsDoc = Utf8GraphQLParser.Parse("{ queryMetrics(metrics: [\"rows\"], groupBy: [\"region\"]) { metrics } }");
        int baseMetricsCost = QueryCostAnalyzerRule.CalculateCost(metricsDoc, schema, costOptions: costOptions);
        var metricsHints = new List<GraphQlIndexHint>();
        int actualMetricsCost = QueryCostAnalyzerRule.CalculateCost(
            metricsDoc,
            schema,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: metricsHints);

        actualMetricsCost.ShouldBe(baseMetricsCost);
        metricsHints.ShouldBeEmpty();

        // Empty hints must NOT add extensions.autheris when executing through the executor
        var cleanExecutor = await CreateBasicExecutorAsync();
        var cleanResult = (OperationResult)await cleanExecutor.ExecuteAsync("{ orders_aggregate { count } }");
        CostAndQuotaMiddleware.AttachIndexHintsToResult(cleanResult, listHints);
        (cleanResult.Extensions?.ContainsKey("autheris") ?? false).ShouldBeFalse();
    }

    private static async Task<IRequestExecutor> CreateBasicExecutorAsync()
    {
        var services = new ServiceCollection();
        var builder = services.AddGraphQLServer().AddDocumentFromString(Sdl);
        foreach (var type in Utf8GraphQLParser.Parse(Sdl).Definitions.OfType<ObjectTypeDefinitionNode>())
        {
            foreach (var field in type.Fields)
            {
                var value = string.Equals(type.Name.Value, "Query", StringComparison.Ordinal)
                    ? (object)new Dictionary<string, object?> { ["count"] = 1L }
                    : 1L;
                builder = builder.AddResolver(type.Name.Value, field.Name.Value, _ => value);
            }
        }
        return await builder.BuildRequestExecutorAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Hint_never_contains_filter_literal_values()
    {
        var schema = await SchemaAsync();
        var ordersMeta = CreateOrdersMetadata(indexCustomerId: false);
        var costOptions = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 2.0 };
        const string secretFilterValue = "TOP_SECRET_FILTER_VALUE_9999";
        const string secretVarValue = "SECRET_VARIABLE_1234";

        var doc = Utf8GraphQLParser.Parse(
            $"query($reg: String) {{ orders_aggregate(where: {{ region: {{ eq: \"{secretFilterValue}\" }}, secret_col: {{ eq: $reg }} }}, groupBy: [region], aggregates: [{{ fn: COUNT, alias: \"c\" }}]) {{ count }} }}");
        var vars = new Dictionary<string, object?> { ["reg"] = secretVarValue };

        var hints = new List<GraphQlIndexHint>();
        QueryCostAnalyzerRule.CalculateCost(
            doc,
            schema,
            variableValues: vars,
            costOptions: costOptions,
            tableMetadataLookup: _ => ordersMeta,
            indexHintsOut: hints);

        hints.ShouldHaveSingleItem();
        var json = JsonSerializer.Serialize(hints);
        json.ShouldNotContain(secretFilterValue);
        json.ShouldNotContain(secretVarValue);
        json.ShouldNotContain("public.orders");
        json.ShouldNotContain("idx_");
        hints[0].Reason.ShouldBe(GraphQlIndexHint.NoUsableIndexReason);
        hints[0].HelpfulIndexColumns.ShouldBe(["region", "secret_col"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CostAndQuotaMiddleware_EnforcesAggregateSurcharge_AndMergesAutherisExtensions()
    {
        // End-to-end middleware execution with CurrentLookup and an authenticated user + IAccessDecisionPoint:
        // charges surcharged aggregate cost, suppresses denied column from helpfulIndexColumns,
        // and merges extensions.autheris.indexHints without clobbering sibling keys inside extensions.autheris.
        var ordersMeta = CreateOrdersMetadata(indexCustomerId: false);
        var rateLimiter = Substitute.For<IRateLimiterService>();
        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(Allowed: true, RemainingTokens: 500, RetryAfterSeconds: 0)));
        rateLimiter.SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(Allowed: true, RemainingTokens: 450, RetryAfterSeconds: 0)));

        var pdp = Substitute.For<IAccessDecisionPoint>();
        var decision = TableAccessDecision.Allowed(
            ordersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["region"] = ColumnAccessLevel.Clear,
                ["secret_col"] = ColumnAccessLevel.Deny
            });
        pdp.DecideAsync(
                Arg.Any<AccessRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(decision));

        static DefaultHttpContext CreateHttpContext() => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "alice"),
                    new Claim(ClaimTypes.PrimarySid, "user-1"),
                    new Claim("sub", "user-1"),
                    new Claim("tenant_id", "tenant-1")
                ],
                "TestAuth"))
        };

        var accessor = new HttpContextAccessor { HttpContext = CreateHttpContext() };
        var gatewayOptions = new GatewayOptions
        {
            QueryCost = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 2.0, EnablePostExecutionSettlement = true }
        };

        var services = new ServiceCollection();
        services.AddSingleton(rateLimiter);
        services.AddSingleton(pdp);
        services.AddSingleton<IHttpContextAccessor>(accessor);
        services.AddSingleton(Options.Create(gatewayOptions));
        services.AddSingleton<IClientTierResolver>(new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(gatewayOptions)));
        services.AddSingleton<IQueryCostQuota, QueryCostQuota>();
        var builder = services.AddGraphQLServer().AddDocumentFromString(Sdl);
        foreach (var type in Utf8GraphQLParser.Parse(Sdl).Definitions.OfType<ObjectTypeDefinitionNode>())
        {
            foreach (var field in type.Fields)
            {
                var value = string.Equals(type.Name.Value, "Query", StringComparison.Ordinal)
                    ? (object)new Dictionary<string, object?> { ["count"] = 1L }
                    : 1L;
                builder = builder.AddResolver(type.Name.Value, field.Name.Value, _ => value);
            }
        }

        var executor = await builder
            .UseRequest<CostAndQuotaMiddleware>()
            .UseRequest(next => async ctx =>
            {
                await next(ctx).ConfigureAwait(false);
                if (ctx.Result is OperationResult res)
                {
                    var ext = res.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
                    res.Extensions = ext.SetItem("autheris", new Dictionary<string, object?>
                    {
                        ["maskedFields"] = new[] { "ssn" }
                    });
                }
            })
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        var prevLookup = CatalogGraphQlTypeModule.CurrentLookup;
        try
        {
            CatalogGraphQlTypeModule.CurrentLookup = name =>
                string.Equals(name, "orders", StringComparison.OrdinalIgnoreCase) ? ordersMeta : null;

            // 1. Visible groupBy column emits helpfulIndexColumns: ["region"] and preserves sibling autheris keys
            var query = "{ orders_aggregate(groupBy: [region], aggregates: [{ fn: COUNT, alias: \"c\" }]) { count } }";
            int unsurcharged = QueryCostAnalyzerRule.CalculateCost(Utf8GraphQLParser.Parse(query), executor.Schema, costOptions: new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 1.0 });

            var result = (OperationResult)await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument(query).Build());
            result.Errors.ShouldBeEmpty();
            result.Extensions.ShouldNotBeNull();
            var extAutheris = result.Extensions!["autheris"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!;
            extAutheris.ShouldContainKey("maskedFields");
            var hints = extAutheris["indexHints"].ShouldBeAssignableTo<IReadOnlyList<GraphQlIndexHint>>()!;
            hints.ShouldHaveSingleItem();
            hints[0].Root.ShouldBe("orders_aggregate");
            hints[0].HelpfulIndexColumns.ShouldBe(["region"]);

            await rateLimiter.Received(1).ReserveCostQuotaAsync(
                Arg.Any<string>(),
                Arg.Is<int>(c => c == unsurcharged * 2),
                Arg.Any<ClientQuotaPolicy>(),
                Arg.Any<CancellationToken>());

            // 2. Denied column in filter triggers all-or-nothing helpfulIndexColumns: [] through middleware PDP evaluation
            accessor.HttpContext = CreateHttpContext();
            var deniedQuery = "{ orders_aggregate(where: { region: { eq: \"EU\" }, secret_col: { eq: \"x\" } }, groupBy: [region], aggregates: [{ fn: COUNT, alias: \"c\" }]) { count } }";
            var deniedResult = (OperationResult)await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument(deniedQuery).Build());
            deniedResult.Errors.ShouldBeEmpty();
            deniedResult.Extensions.ShouldNotBeNull();
            var deniedAutheris = deniedResult.Extensions!["autheris"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!;
            var deniedHints = deniedAutheris["indexHints"].ShouldBeAssignableTo<IReadOnlyList<GraphQlIndexHint>>()!;
            deniedHints.ShouldHaveSingleItem();
            deniedHints[0].HelpfulIndexColumns.ShouldBeEmpty();
        }
        finally
        {
            CatalogGraphQlTypeModule.CurrentLookup = prevLookup;
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Connection_AndAnonymousCallerWithPdp_EnforceFailClosedColumnVisibility()
    {
        var ordersMeta = CreateOrdersMetadata(indexCustomerId: false);
        var rateLimiter = Substitute.For<IRateLimiterService>();
        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(Allowed: true, RemainingTokens: 500, RetryAfterSeconds: 0)));
        rateLimiter.SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(Allowed: true, RemainingTokens: 450, RetryAfterSeconds: 0)));

        var pdp = Substitute.For<IAccessDecisionPoint>();
        var decision = TableAccessDecision.Allowed(
            ordersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["region"] = ColumnAccessLevel.Clear,
                ["secret_col"] = ColumnAccessLevel.Deny
            });
        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(decision));

        static DefaultHttpContext CreateHttpContext(bool authenticated) => new()
        {
            User = authenticated
                ? new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, "alice"),
                        new Claim(ClaimTypes.PrimarySid, "user-1"),
                        new Claim("sub", "user-1"),
                        new Claim("tenant_id", "tenant-1")
                    ],
                    "TestAuth"))
                : new ClaimsPrincipal(new ClaimsIdentity())
        };

        var accessor = new HttpContextAccessor { HttpContext = CreateHttpContext(authenticated: true) };
        var gatewayOptions = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_disable_rate_limiting = true },
            QueryCost = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 2.0 }
        };

        var services = new ServiceCollection();
        services.AddSingleton(rateLimiter);
        services.AddSingleton(pdp);
        services.AddSingleton<IHttpContextAccessor>(accessor);
        services.AddSingleton(Options.Create(gatewayOptions));
        services.AddSingleton<IClientTierResolver>(new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(gatewayOptions)));
        services.AddSingleton<IQueryCostQuota, QueryCostQuota>();
        var builder = services.AddGraphQLServer().AddDocumentFromString(Sdl);
        foreach (var type in Utf8GraphQLParser.Parse(Sdl).Definitions.OfType<ObjectTypeDefinitionNode>())
        {
            foreach (var field in type.Fields)
            {
                object value = type.Name.Value switch
                {
                    "Query" when string.Equals(field.Name.Value, "ordersConnection", StringComparison.Ordinal) =>
                        new Dictionary<string, object?> { ["nodes"] = Array.Empty<object>() },
                    "OrderConnection" when string.Equals(field.Name.Value, "nodes", StringComparison.Ordinal) =>
                        Array.Empty<object>(),
                    "Query" when string.Equals(field.Name.Value, "orders", StringComparison.Ordinal) =>
                        Array.Empty<object>(),
                    "Query" => new Dictionary<string, object?> { ["count"] = 1L },
                    _ => 1L
                };
                builder = builder.AddResolver(type.Name.Value, field.Name.Value, _ => value);
            }
        }

        var executor = await builder
            .UseRequest<CostAndQuotaMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        var prevLookup = CatalogGraphQlTypeModule.CurrentLookup;
        try
        {
            CatalogGraphQlTypeModule.CurrentLookup = name =>
                string.Equals(name, "orders", StringComparison.OrdinalIgnoreCase) ? ordersMeta : null;

            // 1. Relay Connection field strips "Connection" and resolves PDP column visibility (allows clear "region")
            var connClearQuery = "{ ordersConnection(first: 10, where: { region: { eq: \"EU\" } }) { nodes { id } } }";
            var connClearResult = (OperationResult)await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument(connClearQuery).Build());
            connClearResult.Errors.ShouldBeEmpty();
            var connClearHints = connClearResult.Extensions!["autheris"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["indexHints"]
                .ShouldBeAssignableTo<IReadOnlyList<GraphQlIndexHint>>()!;
            connClearHints.ShouldHaveSingleItem();
            connClearHints[0].Root.ShouldBe("ordersConnection");
            connClearHints[0].HelpfulIndexColumns.ShouldBe(["region"]);

            // 2. Relay Connection field with denied "secret_col" suppresses HelpfulIndexColumns (all-or-nothing)
            accessor.HttpContext = CreateHttpContext(authenticated: true);
            var connDeniedQuery = "{ ordersConnection(first: 10, where: { region: { eq: \"EU\" }, secret_col: { eq: \"x\" } }) { nodes { id } } }";
            var connDeniedResult = (OperationResult)await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument(connDeniedQuery).Build());
            connDeniedResult.Errors.ShouldBeEmpty();
            var connDeniedHints = connDeniedResult.Extensions!["autheris"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["indexHints"]
                .ShouldBeAssignableTo<IReadOnlyList<GraphQlIndexHint>>()!;
            connDeniedHints.ShouldHaveSingleItem();
            connDeniedHints[0].HelpfulIndexColumns.ShouldBeEmpty();

            // 3. Anonymous request when IAccessDecisionPoint is registered fails closed -> EmptyVisibleColumns -> []
            accessor.HttpContext = CreateHttpContext(authenticated: false);
            var anonQuery = "{ orders_aggregate(groupBy: [region], aggregates: [{ fn: COUNT, alias: \"c\" }]) { count } }";
            var anonResult = (OperationResult)await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument(anonQuery).Build());
            anonResult.Errors.ShouldBeEmpty();
            var anonHints = anonResult.Extensions!["autheris"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["indexHints"]
                .ShouldBeAssignableTo<IReadOnlyList<GraphQlIndexHint>>()!;
            anonHints.ShouldHaveSingleItem();
            anonHints[0].HelpfulIndexColumns.ShouldBeEmpty();
        }
        finally
        {
            CatalogGraphQlTypeModule.CurrentLookup = prevLookup;
        }
    }
}
