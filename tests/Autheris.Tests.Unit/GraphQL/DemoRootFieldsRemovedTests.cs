using Autheris.Api.Extensions;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// RDF-S5 / RDF-S8: the demo root fields <c>finance</c> and <c>hr</c> (and the types only they reached) are gone.
/// Selecting them is a GraphQL validation error, never an internal error.
/// </summary>
public sealed class DemoRootFieldsRemovedTests
{
    private static async Task<IRequestExecutor> BuildExecutorAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IRateLimiterService>());
        services.AddSingleton(Substitute.For<Autheris.Application.Governance.Interfaces.ISchemaSunsettingService>());
        services.AddSingleton(Substitute.For<Autheris.Application.Dbt.Interfaces.IDbtHealthCircuitBreaker>());
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(Substitute.For<IHostEnvironment>());
        services.AddSingleton(Substitute.For<ITableMetadataRepository>());
        services.AddSingleton(Substitute.For<ITableRelationRepository>());
        services.AddSingleton(Substitute.For<IHttpContextAccessor>());
        services.AddSingleton<IOptions<AnalyticsOptions>>(Options.Create(new AnalyticsOptions()));
        services.AddGatewayGraphQL(new GatewayOptions());

        var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IRequestExecutorProvider>();
        return await resolver.GetExecutorAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Schema_HasNoFinanceOrHrRootFields()
    {
        var executor = await BuildExecutorAsync();
        var sdl = executor.Schema.ToString();

        sdl.ShouldNotContain("finance:");
        sdl.ShouldNotContain("hr:");
        sdl.ShouldNotContain("FinanceQuery");
        sdl.ShouldNotContain("HrQuery");
        sdl.ShouldNotContain("InvoiceRecord");
        sdl.ShouldNotContain("InvoiceItemRecord");
        sdl.ShouldContain("table(");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("{ finance { __typename } }")]
    [InlineData("{ hr { __typename } }")]
    [InlineData("{ ... on Query { finance { __typename } } }")]
    [InlineData("query { ...F } fragment F on Query { hr { __typename } }")]
    public async Task FinanceOrHrRootSelection_FailsValidation(string query)
    {
        var executor = await BuildExecutorAsync();

        var result = await executor.ExecuteAsync(query);

        var op = result.ExpectOperationResult();
        op.Errors.ShouldNotBeNull();
        op.Errors.ShouldNotBeEmpty();
        op.Data.ShouldBeNull();
        op.Errors.ShouldContain(e => e.Code == "GRAPHQL_VALIDATION_FAILED", string.Join(" | ", op.Errors.Select(e => e.Code + ":" + e.Message)));
        op.Errors.ShouldNotContain(e => string.Equals(e.Code, "INTERNAL", StringComparison.OrdinalIgnoreCase));
        op.Errors.ShouldNotContain(e => e.Exception != null);
    }
}
