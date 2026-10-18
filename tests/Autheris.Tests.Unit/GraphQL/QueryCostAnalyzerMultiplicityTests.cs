namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Analysis;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Collection("CatalogRelation")]
public sealed class QueryCostAnalyzerMultiplicityTests : IDisposable
{
    public sealed class Address
    {
        public string City { get; set; } = "Berlin";
    }

    public sealed class Customer
    {
        public string Id { get; set; } = "cust-1";
        public Address Address { get; set; } = new();
    }

    public sealed class Order
    {
        public string Id { get; set; } = "order-1";
        public Customer Customer { get; set; } = new();
        public Customer UncatalogedCustomer { get; set; } = new();
    }

    public sealed class Query
    {
        public List<Order> GetOrders(int first = 10) => [new()];
    }

    public QueryCostAnalyzerMultiplicityTests()
    {
        CatalogRelationRegistry.Clear();
    }

    public void Dispose()
    {
        CatalogRelationRegistry.Clear();
    }

    private static async Task<(IRequestExecutorProvider Provider, ISchemaDefinition Schema)> CreateTestSchemaAsync()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer()
            .AddQueryType<Query>();

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IRequestExecutorProvider>();
        var executor = await provider.GetExecutorAsync();
        return (provider, executor.Schema);
    }

    [Fact]
    public async Task MultiplicityAware_OneToOneAndManyToOne_UsesMultiplierOne()
    {
        var (_, schema) = await CreateTestSchemaAsync();

        // Register relations:
        // Order.customer is ManyToOne
        // Customer.address is OneToOne
        CatalogRelationRegistry.Register("Order", "customer", RelationCardinality.ManyToOne);
        CatalogRelationRegistry.Register("Customer", "address", RelationCardinality.OneToOne);

        var doc = Utf8GraphQLParser.Parse(@"
            query {
                orders(first: 10) {
                    id
                    customer {
                        id
                        address {
                            city
                        }
                    }
                }
            }");

        var costOptions = new QueryCostOptions
        {
            CostPerSingleRowLookup = 1,
            DefaultListMultiplier = 10
        };

        int cost = QueryCostAnalyzerRule.CalculateCost(
            doc,
            schema,
            defaultListMultiplier: 10,
            maxResponseRows: 1000,
            costOptions: costOptions);

        // Without multiplicity awareness:
        // orders (10) * customer (10) * address (10) would be ~1000+
        // With multiplicity awareness:
        // customer and address have multiplier 1 rather than 10
        // Total cost should be well below 200
        cost.ShouldBeLessThan(200);
    }

    [Fact]
    public async Task UncatalogedObjectField_FailsClosedToDefaultListMultiplier()
    {
        var (_, schema) = await CreateTestSchemaAsync();

        // Register ONLY customer as ManyToOne
        CatalogRelationRegistry.Register("Order", "customer", RelationCardinality.ManyToOne);
        CatalogRelationRegistry.Register("Customer", "address", RelationCardinality.OneToOne);
        // Do NOT register Order.uncatalogedCustomer

        var docVerified = Utf8GraphQLParser.Parse(@"
            query {
                orders(first: 10) {
                    customer {
                        id
                    }
                }
            }");

        var docUnverified = Utf8GraphQLParser.Parse(@"
            query {
                orders(first: 10) {
                    uncatalogedCustomer {
                        id
                    }
                }
            }");

        int costVerified = QueryCostAnalyzerRule.CalculateCost(docVerified, schema);
        int costUnverified = QueryCostAnalyzerRule.CalculateCost(docUnverified, schema);

        // Unverified field must fail closed and incur higher cost (DefaultListMultiplier = 10 vs 1)
        costUnverified.ShouldBeGreaterThan(costVerified);
    }

    [Fact]
    public async Task ParityBetweenQueryCostAnalyzerRule_And_CostAnalysisEngine()
    {
        var (provider, schema) = await CreateTestSchemaAsync();

        CatalogRelationRegistry.Register("Order", "customer", RelationCardinality.ManyToOne);
        CatalogRelationRegistry.Register("Customer", "address", RelationCardinality.OneToOne);

        var doc = Utf8GraphQLParser.Parse(@"
            query {
                orders(first: 10) {
                    id
                    customer {
                        address {
                            city
                        }
                    }
                }
            }");

        var gatewayOptions = new GatewayOptions();
        var engine = new HotChocolateGraphQlCostAnalysisEngine(provider, Options.Create(gatewayOptions));

        var engineResult = await engine.CalculateCostAsync(doc);
        var ruleCost = QueryCostAnalyzerRule.CalculateCost(doc, schema, costOptions: gatewayOptions.QueryCost);

        engineResult.TotalCost.ShouldBe(ruleCost);
    }
}

[CollectionDefinition("CatalogRelation", DisableParallelization = true)]
public sealed class CatalogRelationCollectionDefinition
{
}
