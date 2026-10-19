using System.Security.Claims;
using Autheris.Api.Middleware;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#112 FR-112-7 / C-9 / T-S-10: a read-only token runs the typed aggregate fields through the real middleware pair, and nothing else.</summary>
public sealed class ReadOnlyTokenAggregateTests
{
    private const string Aggregate = "{ sales_dbo_orders_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count } }";
    private const string Metrics = "{ queryMetrics(metrics: [\"revenue\"]) { count } }";

    private static ClaimsPrincipal ReadOnlyPrincipal()
    {
        var identity = new ClaimsIdentity([new Claim("oid", "33333333-3333-3333-3333-333333333333"), new Claim("tenant_id", "T1")], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        return new ClaimsPrincipal(identity);
    }

    public sealed class TouchMutation
    {
        public bool Touch() => true;
    }

    private sealed class Rig
    {
        public IGovernedAggregationService Aggregation { get; } = Substitute.For<IGovernedAggregationService>();

        public ISemanticMetricQueryService MetricService { get; } = Substitute.For<ISemanticMetricQueryService>();

        public IGovernedTreeQueryService Tree { get; } = Substitute.For<IGovernedTreeQueryService>();

        public async Task<IRequestExecutor> BuildAsync(ClaimsPrincipal principal)
        {
            Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
                .Returns(_ => TypedAggregateTestHost.Result([], 3));
            MetricService.QueryMetricsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<MetricQueryRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => new MetricQueryResult(
                    ["revenue"], "v1", [],
                    new AggregationGroupRow(new Dictionary<string, object?>(), 2, new Dictionary<string, object?>()),
                    new AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, false),
                    new Dictionary<string, Autheris.Application.Dbt.Services.MetricDefinitionSummary>()));

            var table = TypedAggregateTestHost.DefaultTable();
            var metadataRepo = Substitute.For<ITableMetadataRepository>();
            metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([table]);
            var relationRepo = Substitute.For<ITableRelationRepository>();
            relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);
            var accessor = Substitute.For<IHttpContextAccessor>();
            accessor.HttpContext.Returns(new DefaultHttpContext { User = principal });

            return await new ServiceCollection()
                .AddSingleton(Aggregation)
                .AddSingleton(MetricService)
                .AddSingleton(Tree)
                .AddSingleton(accessor)
                .AddSingleton(Options.Create(new AnalyticsOptions()))
                .AddGraphQLServer()
                .AddQueryType<Query>()
                .AddMutationType<TouchMutation>()
                .AddTypeModule(_ => new CatalogGraphQlTypeModule(metadataRepo, relationRepo))
                .UseDocumentParser()
                .UseDocumentValidation()
                .UseRequest<ReadOnlyOperationMiddleware>()
                .UseOperationResolver()
                .UseOperationVariableCoercion()
                .UseOperationExecution()
                .BuildRequestExecutorAsync();
        }
    }

    private static async Task<IExecutionResult> RunAsync(Rig rig, ClaimsPrincipal principal, string document, string? operationName = null)
    {
        var executor = await rig.BuildAsync(principal);
        return await executor.ExecuteAsync(OperationRequestBuilder.New()
            .SetDocument(document)
            .SetOperationName(operationName)
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build());
    }

    [Fact]
    public async Task ReadOnlyToken_RunsTheTypedAggregateField_ThroughTheMiddleware()
    {
        var rig = new Rig();

        var result = await RunAsync(rig, ReadOnlyPrincipal(), Aggregate);

        TypedAggregateTestHost.Errors(result).ShouldBeEmpty();
        await rig.Aggregation.Received(1).ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadOnlyToken_RunsQueryMetrics_ThroughTheMiddleware()
    {
        var rig = new Rig();

        var result = await RunAsync(rig, ReadOnlyPrincipal(), Metrics);

        TypedAggregateTestHost.Errors(result).ShouldBeEmpty();
        await rig.MetricService.Received(1).QueryMetricsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<MetricQueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ReadOnlyToken_StillGetsReadOnlyTokenForAMutation_AlsoInAMixedDocument_C9()
    {
        var rig = new Rig();

        var mutation = await RunAsync(rig, ReadOnlyPrincipal(), "mutation { touch }");
        var mixed = await RunAsync(rig, ReadOnlyPrincipal(), "query Q { sales_dbo_orders_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count } } mutation M { touch }", "M");

        TypedAggregateTestHost.Errors(mutation).Select(e => e.Code).ShouldContain("READ_ONLY_TOKEN");
        TypedAggregateTestHost.Errors(mixed).Select(e => e.Code).ShouldContain("READ_ONLY_TOKEN");
        await rig.Aggregation.DidNotReceive().ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("POST", "/api/extensions/dbt/semantic-manifest")]
    [InlineData("POST", "/api/extensions/dbt/sync")]
    [InlineData("POST", "/api/extensions/dbt/governance")]
    [InlineData("PUT", "/api/v1/analytics/metrics")]
    public async Task ReadOnlyToken_CannotReachAnImportOrAnyOtherWriteRoute_C9(string method, string path)
    {
        var passed = false;
        var middleware = new ReadOnlyTokenMiddleware(_ => { passed = true; return Task.CompletedTask; }, "/graphql", "/mcp");
        var context = new DefaultHttpContext { User = ReadOnlyPrincipal() };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        passed.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ReadOnlyToken_GraphQlEndpoint_PassesTheMiddleware()
    {
        var passed = false;
        var middleware = new ReadOnlyTokenMiddleware(_ => { passed = true; return Task.CompletedTask; }, "/graphql", "/mcp");
        var context = new DefaultHttpContext { User = ReadOnlyPrincipal() };
        context.Request.Method = "POST";
        context.Request.Path = "/graphql";

        await middleware.InvokeAsync(context);

        passed.ShouldBeTrue();
    }
}
