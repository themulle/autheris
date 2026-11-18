using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public class CoreMetricModelArchitectureTests
{
    [Fact]
    public void Metric_model_has_no_dbt_dependency()
    {
        var result = Types.InAssembly(typeof(Autheris.Application.Catalog.Semantic.ISemanticMetricImport).Assembly)
            .That().ResideInNamespace("Autheris.Application.Catalog.Semantic")
            .ShouldNot().HaveDependencyOnAny("Autheris.Extensions", "Autheris.Modules")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Analytics_and_mcp_semantic_consumers_reference_only_the_semantic_namespace()
    {
        var api = Types.InAssembly(typeof(Autheris.Api.Endpoints.AnalyticsEndpoints).Assembly)
            .That().HaveName("AnalyticsEndpoints")
            .ShouldNot().HaveDependencyOn("Autheris.Extensions.Dbt")
            .GetResult();
        var mcp = Types.InAssembly(typeof(Autheris.Application.Mcp.Services.McpSemanticTools).Assembly)
            .That().HaveName("McpSemanticTools")
            .ShouldNot().HaveDependencyOn("Autheris.Extensions.Dbt")
            .GetResult();

        api.IsSuccessful.ShouldBeTrue();
        mcp.IsSuccessful.ShouldBeTrue();
    }
}
