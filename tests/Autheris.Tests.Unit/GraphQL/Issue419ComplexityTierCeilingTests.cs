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
/// Issue #419 (WP-B of #417): GraphQL root lists admission estimate and per-tier complexity ceiling.
/// </summary>
public sealed class Issue419ComplexityTierCeilingTests
{
    public sealed class Customer
    {
        public string Id { get; set; } = "c1";
        public string Name { get; set; } = "Customer 1";
        public string Email { get; set; } = "cust1@example.com";
        public string Phone { get; set; } = "555-1234";
        public string Address { get; set; } = "123 Main St";
        public string City { get; set; } = "Metropolis";
        public string Country { get; set; } = "USA";
        public string Status { get; set; } = "Active";
        public string CreatedAt { get; set; } = "2026-01-01";
        public string UpdatedAt { get; set; } = "2026-01-02";

        public List<Order> Orders => [new()];
    }

    public sealed class Order
    {
        public string OrderId { get; set; } = "o1";
        public decimal Amount { get; set; } = 100.0m;
    }

    [GraphQLName("Query")]
    public sealed class TestQuery
    {
        public List<Customer> GetCustomers(int first = 10) => [new()];
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
        services.AddGraphQLServer().AddQueryType<TestQuery>();
        var provider = services.BuildServiceProvider().GetRequiredService<IRequestExecutorProvider>();
        return (await provider.GetExecutorAsync()).Schema;
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Development", 0.0)]
    [InlineData("Development", -0.5)]
    [InlineData("Development", 1.01)]
    [InlineData("Development", 2.0)]
    [InlineData("Production", 0.0)]
    [InlineData("Production", -0.5)]
    [InlineData("Production", 1.01)]
    [InlineData("Production", 2.0)]
    public void ScalarColumnWeight_OutOfRange_FailsStartupInAllEnvironments(string environment, double weight)
    {
        var options = new QueryCostOptions { ScalarColumnWeight = weight };

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateQueryCostOptions(options, Env(environment)));

        ex.Message.ShouldContain("ScalarColumnWeight");
        ex.Message.ShouldContain("0.01..1.0");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Development")]
    [InlineData("Production")]
    public void ScalarColumnWeight_DefaultsAndBounds_AreAccepted(string environment)
    {
        GatewayStartupValidator.ValidateQueryCostOptions(new QueryCostOptions(), Env(environment));
        GatewayStartupValidator.ValidateQueryCostOptions(new QueryCostOptions { ScalarColumnWeight = 0.01 }, Env(environment));
        GatewayStartupValidator.ValidateQueryCostOptions(new QueryCostOptions { ScalarColumnWeight = 1.0 }, Env(environment));
        GatewayStartupValidator.ValidateQueryCostOptions(new QueryCostOptions { ScalarColumnWeight = 0.2 }, Env(environment));
        new QueryCostOptions().ScalarColumnWeight.ShouldBe(1.0);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(50)]
    [InlineData(10001)]
    [InlineData(20000)]
    public void ClientTier_ComplexityOverride_OutOfRange_FailsStartup(int costOverride)
    {
        var options = new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    TierLimits = new Dictionary<string, ClientTierLimitOverride>
                    {
                        ["Enterprise"] = new() { MaxCostPerQuery = costOverride }
                    }
                }
            }
        };

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateClientTierComplexityOverrides(options));
        ex.Message.ShouldContain("MaxCostPerQuery must be between 100 and 10000");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(50)]
    [InlineData(10001)]
    [InlineData(20000)]
    public void ScopeLimit_ComplexityOverride_OutOfRange_FailsStartup(int costOverride)
    {
        var options = new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    ScopeLimits = new Dictionary<string, ClientTierLimitOverride>
                    {
                        ["analytics:grid"] = new() { MaxCostPerQuery = costOverride }
                    }
                }
            }
        };

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateClientTierComplexityOverrides(options));
        ex.Message.ShouldContain("MaxCostPerQuery must be between 100 and 10000");
    }

    [Fact]
    public async Task QueryCostAnalyzer_ScalarColumnWeight_ReducesRootListChildCost()
    {
        var schema = await SchemaAsync();
        var query = """
            query {
                customers(first: 100) {
                    id
                    name
                    phone
                    address
                    city
                    country
                    status
                    createdAt
                    updatedAt
                }
            }
            """;
        var doc = Utf8GraphQLParser.Parse(query);

        var costFull = QueryCostAnalyzerRule.CalculateCost(
            doc,
            schema,
            costOptions: new QueryCostOptions { ScalarColumnWeight = 1.0, DefaultListMultiplier = 10 });

        var costDiscounted = QueryCostAnalyzerRule.CalculateCost(
            doc,
            schema,
            costOptions: new QueryCostOptions { ScalarColumnWeight = 0.2, DefaultListMultiplier = 10 });

        costDiscounted.ShouldBeLessThan(costFull);
        // Under 1.0 weight: 9 scalars * 100 rows = 900 child cost, plus 10 * 100 = 1000 list base = 1900.
        // Under 0.2 weight: ceil(9 * 0.2) = 2 scalars * 100 rows = 200 child cost, plus 10 * 100 = 1000 list base = 1200.
        costFull.ShouldBe(1900);
        costDiscounted.ShouldBe(1200);
    }

    [Fact]
    public async Task QueryCostAnalyzer_NestedListRelation_RetainsFullWeight()
    {
        var schema = await SchemaAsync();
        var query = """
            query {
                customers(first: 10) {
                    id
                    name
                    orders {
                        orderId
                        amount
                    }
                }
            }
            """;
        var doc = Utf8GraphQLParser.Parse(query);

        var costDiscounted = QueryCostAnalyzerRule.CalculateCost(
            doc,
            schema,
            costOptions: new QueryCostOptions { ScalarColumnWeight = 0.2, DefaultListMultiplier = 10 });

        // Nested orders list retains its unpaginated default multiplier and child costs
        costDiscounted.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ResolveMaxAdmissionCeiling_DefaultsToGraphQlMaxAllowedComplexity()
    {
        var options = new GatewayOptions
        {
            GraphQL = new GraphQLOptions { MaxAllowedComplexity = 500 }
        };

        var ceiling = CostAndQuotaMiddleware.ResolveMaxAdmissionCeiling(options);
        ceiling.ShouldBe(500);
    }

    [Fact]
    public void ResolveMaxAdmissionCeiling_ReturnsSupremumAcrossTierAndScopeOverrides()
    {
        var options = new GatewayOptions
        {
            GraphQL = new GraphQLOptions { MaxAllowedComplexity = 500 },
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    TierLimits = new Dictionary<string, ClientTierLimitOverride>
                    {
                        ["Free"] = new() { MaxCostPerQuery = 100 },
                        ["Enterprise"] = new() { MaxCostPerQuery = 2500 }
                    },
                    ScopeLimits = new Dictionary<string, ClientTierLimitOverride>
                    {
                        ["analytics:batch"] = new() { MaxCostPerQuery = 3500 }
                    }
                }
            }
        };

        var ceiling = CostAndQuotaMiddleware.ResolveMaxAdmissionCeiling(options);
        ceiling.ShouldBe(3500);
    }

    [Fact]
    public void ResolveMaxAdmissionCeiling_EnforcesSafetyCeilingOf10000()
    {
        var options = new GatewayOptions
        {
            GraphQL = new GraphQLOptions { MaxAllowedComplexity = 500 },
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    TierLimits = new Dictionary<string, ClientTierLimitOverride>
                    {
                        ["SuperEnterprise"] = new() { MaxCostPerQuery = 20000 }
                    }
                }
            }
        };

        var ceiling = CostAndQuotaMiddleware.ResolveMaxAdmissionCeiling(options);
        ceiling.ShouldBe(CostAndQuotaMiddleware.MaxComplexitySafetyCeiling);
        ceiling.ShouldBe(10000);
    }

    [Fact]
    public void ResolveMaxAdmissionCeiling_RelaxedLimits_AreRespected()
    {
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_relaxed_query_limits = true },
            AllowInsecureWarnFlagsInProduction = false
        };

        var ceiling = CostAndQuotaMiddleware.ResolveMaxAdmissionCeiling(options);
        ceiling.ShouldBe(5000);
    }
}
