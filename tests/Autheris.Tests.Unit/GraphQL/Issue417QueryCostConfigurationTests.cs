namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#417 WP-A: GraphQL query cost configuration (I417-SC-A01..A09).</summary>
[Collection("CatalogRelation")]
public sealed class Issue417QueryCostConfigurationTests
{
    public sealed class Item
    {
        public string Id { get; set; } = "1";
    }

    [GraphQLName("Query")]
    public sealed class CostQuery
    {
        public List<Item> GetItems(int first = 10) => [new()];

        public string Public() => "ok";
    }

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static async Task<ISchemaDefinition> SchemaAsync()
    {
        var executor = await new ServiceCollection().AddGraphQLServer().AddQueryType<CostQuery>().BuildRequestExecutorAsync();
        return executor.Schema;
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Development", 0)]
    [InlineData("Development", -1)]
    [InlineData("Development", 1001)]
    [InlineData("Development", int.MaxValue)]
    [InlineData("Production", 0)]
    [InlineData("Production", -1)]
    [InlineData("Production", 1001)]
    [InlineData("Production", int.MaxValue)]
    public void I417_T_SA01_QueryCostValidation_MultiplierOutOfRange_FailsStartupInAllEnvironments(string environment, int multiplier)
    {
        var options = new QueryCostOptions { DefaultListMultiplier = multiplier };

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateQueryCostOptions(options, Env(environment)));

        ex.Message.ShouldContain("DefaultListMultiplier");
        ex.Message.ShouldContain("1..1000");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Development")]
    [InlineData("Production")]
    public void I417_T_SA01_QueryCostValidation_DefaultsAndBounds_AreAccepted(string environment)
    {
        GatewayStartupValidator.ValidateQueryCostOptions(new QueryCostOptions(), Env(environment));
        GatewayStartupValidator.ValidateQueryCostOptions(new QueryCostOptions { DefaultListMultiplier = 1 }, Env(environment));
        GatewayStartupValidator.ValidateQueryCostOptions(new QueryCostOptions { DefaultListMultiplier = 1000 }, Env(environment));
        new QueryCostOptions().DefaultListMultiplier.ShouldBe(10);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Development")]
    [InlineData("Production")]
    public void I417_T_SA02_GovernanceQueryCostLegacyKey_IsRejectedByUnknownKeyGuard(string environment)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Governance:QueryCost:DefaultListMultiplier"] = "SECRET-VALUE-4711" });
        DevConfiguration.Apply(configuration, Env(environment));

        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration));

        ex.Message.ShouldContain("Gateway:Governance:QueryCost");
        ex.Message.ShouldNotContain("SECRET-VALUE-4711");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void I417_SC_A05_CanonicalSection_IsGatewayQueryCost_AndAccepted()
    {
        QueryCostOptions.SectionName.ShouldBe("Gateway:QueryCost");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:QueryCost:DefaultListMultiplier"] = "3" })
            .Build();

        Should.NotThrow(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task I417_T_SA03_ConfiguredListMultiplier_AdmissionAndMiddleware_UseSamePrice()
    {
        var schema = await SchemaAsync();
        var gateway = new GatewayOptions { QueryCost = new QueryCostOptions { DefaultListMultiplier = 3 } };
        var doc = Utf8GraphQLParser.Parse("{ items(first: 100) { id } }");

        var rule = GatewayServiceCollectionExtensions.CreateQueryCostRule(gateway, 100000, null);
        int admission = rule.ComputeCost(doc, schema);

        // CostAndQuotaMiddleware prices through CalculateCost with the same gatewayOptions.QueryCost instance.
        int middleware = QueryCostAnalyzerRule.CalculateCost(doc, schema, maxResponseRows: gateway.GraphQL.MaxResponseRows, costOptions: gateway.QueryCost);
        int defaultPrice = QueryCostAnalyzerRule.CalculateCost(doc, schema, maxResponseRows: gateway.GraphQL.MaxResponseRows, costOptions: new QueryCostOptions());

        admission.ShouldBe(middleware);
        admission.ShouldNotBe(defaultPrice);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(0)]
    [InlineData(1001)]
    public void I417_SC_A02_RuleRejectsOutOfRangeMultiplier_InsteadOfSilentFallback(int multiplier)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new QueryCostAnalyzerRule(100, costOptions: new QueryCostOptions { DefaultListMultiplier = multiplier }));
    }

    private static async Task<string> ShieldJsonAsync(int multiplier)
    {
        var env = Env("Production");
        var gateway = new GatewayOptions { QueryCost = new QueryCostOptions { DefaultListMultiplier = multiplier } };
        var executor = await new ServiceCollection()
            .AddSingleton(env)
            .AddSingleton(sp => new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance))
            .AddGraphQLServer()
            .AddApplicationService<IHostEnvironment>()
            .AddApplicationService<ErrorSanitizingFilter>()
            .AddErrorFilter(sp => sp.GetRequiredService<ErrorSanitizingFilter>())
            .AddQueryType<CostQuery>()
            .AddValidationRule<QueryCostAnalyzerRule>((_, _) => GatewayServiceCollectionExtensions.CreateQueryCostRule(gateway, 100000, null))
            .UseRequest<GraphQlEnumerationShieldMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();
        await using var result = await executor.ExecuteAsync("{ salaries }");
        return result.ToJson();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task I417_T_SA04_ProductionEnumerationShield_InvalidDocument_ErrorBytesUnchanged()
    {
        var baseline = await ShieldJsonAsync(10);
        var lowered = await ShieldJsonAsync(3);

        lowered.ShouldBe(baseline);
        baseline.ShouldContain("GRAPHQL_VALIDATION_FAILED");
        baseline.ShouldNotContain("salaries");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task I417_T_SA05_LoweredMultiplier_StillBoundedByMaxResponseRowsAndComplexity()
    {
        var schema = await SchemaAsync();
        var gateway = new GatewayOptions { QueryCost = new QueryCostOptions { DefaultListMultiplier = 1 } };
        var one = Utf8GraphQLParser.Parse("{ a: items { id } }");
        var many = Utf8GraphQLParser.Parse(string.Join(' ', ["{", .. Enumerable.Range(0, 20).Select(i => $"a{i}: items {{ id }}"), "}"]));

        var rule = GatewayServiceCollectionExtensions.CreateQueryCostRule(gateway, 100000, null);

        rule.ComputeCost(one, schema).ShouldBeGreaterThanOrEqualTo(1);
        // Cost still grows with every list and is capped only by MaxAllowedComplexity at admission.
        rule.ComputeCost(many, schema).ShouldBeGreaterThan(rule.ComputeCost(one, schema));
        gateway.QueryCost.EnablePostExecutionSettlement.ShouldBeTrue();
    }

    private static TableMetadata Table() => new()
    {
        Identifier = new TableIdentifier("sales", "dbo", "customers"),
        Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
        PrimaryKeyColumns = ["id"],
    };

    private static async Task<string?> RootListDescriptionAsync(int maxAllowedOffset)
    {
        var meta = Substitute.For<ITableMetadataRepository>();
        var rel = Substitute.For<ITableRelationRepository>();
        meta.GetAllTablesAsync(Arg.Any<System.Threading.CancellationToken>()).Returns([Table()]);
        rel.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<System.Threading.CancellationToken>()).Returns([]);
        var services = new ServiceCollection();
        services.AddSingleton(meta);
        services.AddSingleton(rel);
        services.AddSingleton(Substitute.For<Autheris.Application.Sql.Tree.IGovernedTreeQueryService>());
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "a")], "t")) } });
        services.AddSingleton(Options.Create(new GatewayOptions()));
        var executor = await services
            .AddGraphQLServer()
            .AddQueryType<CostQuery>()
            .UseDefaultPipeline()
            .AddTypeModule(_ => new CatalogGraphQlTypeModule(meta, rel, graphQlOptions: new GraphQLOptions { MaxAllowedOffset = maxAllowedOffset }))
            .BuildRequestExecutorAsync();
        return executor.Schema.QueryType.Fields["sales_dbo_customers"].Description;
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(0, "Offset pagination is not supported.")]
    [InlineData(100, "Offset pagination is supported up to offset 100.")]
    [InlineData(10000, "Offset pagination is supported up to offset 10000.")]
    public async Task I417_T_SA06_RootListDescription_UsesConfiguredMaxAllowedOffset_NoOtherConfigLeaks(int offset, string expected)
    {
        var description = await RootListDescriptionAsync(offset);

        description.ShouldNotBeNull();
        description.ShouldEndWith(expected);
        if (offset != 10000)
        {
            description.ShouldNotContain("10000");
        }
    }

    [Fact]
    public void BuildOffsetDescription_IsCultureInvariant()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            CatalogGraphQlTypeModule.BuildOffsetDescription(1234).ShouldBe("Offset pagination is supported up to offset 1234.");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
