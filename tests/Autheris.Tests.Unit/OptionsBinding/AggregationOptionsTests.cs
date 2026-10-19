using Autheris.Api.Configuration.Validation;
using Autheris.Application.Dbt.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.OptionsBinding;

/// <summary>#112 option classes: defaults, binding and fail-fast ranges (SEC-112-19, A-12).</summary>
public sealed class AggregationOptionsTests
{
    [Fact]
    public void GraphQLAggregationOptions_Defaults()
    {
        var o = new GraphQLOptions().Aggregation;

        o.Enabled.ShouldBeTrue();
        o.MaxFieldsPerOperation.ShouldBe(3);
    }

    [Fact]
    public void GraphQLAggregationOptions_BindFromGatewayConfiguration()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:GraphQL:Aggregation:Enabled"] = "false",
            ["Gateway:GraphQL:Aggregation:MaxFieldsPerOperation"] = "7"
        }).Build();
        var gateway = new GatewayOptions();
        cfg.GetSection("Gateway").Bind(gateway);

        gateway.GraphQL.Aggregation.Enabled.ShouldBeFalse();
        gateway.GraphQL.Aggregation.MaxFieldsPerOperation.ShouldBe(7);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public void MaxFieldsPerOperation_OutOfRange_FailsStartup(int value)
    {
        var options = new GatewayOptions { GraphQL = new GraphQLOptions { Aggregation = new GraphQLAggregationOptions { MaxFieldsPerOperation = value } } };

        var result = new GraphQLAggregationOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("MaxFieldsPerOperation");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void MaxFieldsPerOperation_InRange_Succeeds(int value)
    {
        var options = new GatewayOptions { GraphQL = new GraphQLOptions { Aggregation = new GraphQLAggregationOptions { MaxFieldsPerOperation = value } } };

        new GraphQLAggregationOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void SemanticOptions_Defaults()
    {
        var o = new SemanticOptions();

        o.PersistManifest.ShouldBeTrue();
        o.ManifestCacheSeconds.ShouldBe(30);
        o.MaxFilterTemplateLength.ShouldBe(2048);
        SemanticOptions.SectionName.ShouldBe("Semantic");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void SemanticOptions_ManifestCacheSeconds_OutOfRange_Fails(int value) =>
        new SemanticOptionsValidator().Validate(null, new SemanticOptions { ManifestCacheSeconds = value }).Failed.ShouldBeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(3600)]
    public void SemanticOptions_ManifestCacheSeconds_InRange_Succeeds(int value) =>
        new SemanticOptionsValidator().Validate(null, new SemanticOptions { ManifestCacheSeconds = value }).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(65537)]
    public void SemanticOptions_MaxFilterTemplateLength_OutOfRange_Fails(int value) =>
        new SemanticOptionsValidator().Validate(null, new SemanticOptions { MaxFilterTemplateLength = value }).Failed.ShouldBeTrue();
}
