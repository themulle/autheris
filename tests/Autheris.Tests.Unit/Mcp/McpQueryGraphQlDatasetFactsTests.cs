using System.Text.Json;
using System.Linq;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestSupport;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;
using HotChocolate.Features;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Mcp;

public sealed class McpQueryGraphQlDatasetFactsTests
{
    private static McpToolDefinition Tool() =>
        McpDatasetTools.Definitions().Single(d => d.Name == "query_graphql");

    private static McpSessionContext Session() => new(
        "s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["Analyst"]);

    private static async Task<(IOperationRequest? Request, string Json)> RunAsync(string args)
    {
        var requestExecutor = new CapturingExecutor();
        var provider = new FixedExecutorProvider(requestExecutor);
        var executor = new GatewayMcpQueryExecutor(provider, new StubGatewayExecutionService(), NullLogger<GatewayMcpQueryExecutor>.Instance,
            McpRowCapTests.Options(100), datasetCatalog: StubMcpDatasetCatalog.Empty, persistedToolValidator: new PersistedToolValidator());
        var json = await executor.ExecuteOperationAsync(Tool(), args, Session());
        return (requestExecutor.Captured, json);
    }

    [Fact]
    public async Task DatasetFactsTrue_ForwardsRequestExtension()
    {
        var (request, _) = await RunAsync("""{"query":"{ a { id } }","datasetFacts":true}""");

        request.ShouldNotBeNull();
        Autheris.GraphQL.Interceptors.DatasetFactsRequest.IsRequested(request).ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"query":"{ a { id } }"}""")]
    [InlineData("""{"query":"{ a { id } }","datasetFacts":false}""")]
    public async Task DatasetFactsAbsent_NoRequestExtension(string args)
    {
        var (request, _) = await RunAsync(args);

        request.ShouldNotBeNull();
        Autheris.GraphQL.Interceptors.DatasetFactsRequest.IsRequested(request).ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"query":"{ a { id } }","datasetFacts":"yes-secret-marker"}""")]
    [InlineData("""{"query":"{ a { id } }","datasetFacts":1}""")]
    [InlineData("""{"query":"{ a { id } }","datasetFacts":{"x":1}}""")]
    public async Task DatasetFactsNotBoolean_InvalidParams(string args)
    {
        var (request, json) = await RunAsync(args);

        request.ShouldBeNull();
        json.ShouldContain("INVALID_PARAMS");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DatasetFactsNotBoolean_DoesNotEchoValue()
    {
        var (_, json) = await RunAsync("""{"query":"{ a { id } }","datasetFacts":"yes-secret-marker"}""");

        json.ShouldNotContain("yes-secret-marker");
    }

    [Fact]
    public void ToolSchema_AdvertisesDatasetFactsBoolean()
    {
        using var schema = JsonDocument.Parse(Tool().InputJsonSchema);

        schema.RootElement.GetProperty("properties").GetProperty("datasetFacts").GetProperty("type").GetString().ShouldBe("boolean");
        schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(["query"]);
    }

    [Fact]
    public void ToolDescription_StatesTruncatedScopeAndFlagMeaning()
    {
        using var schema = JsonDocument.Parse(Tool().InputJsonSchema);
        var description = schema.RootElement.GetProperty("properties").GetProperty("datasetFacts").GetProperty("description").GetString()!;

        description.ShouldContain("row filter defined by the data owner or by consent");
        description.ShouldContain("does not mean all rows are returned");
        description.ShouldContain("governed limit");
        description.ShouldContain("response size cap is not included");
    }

    private sealed class FixedExecutorProvider(IRequestExecutor executor) : IRequestExecutorProvider
    {
        public ImmutableArray<string> SchemaNames { get; } = [ISchemaDefinition.DefaultName];

        public ValueTask<IRequestExecutor> GetExecutorAsync(string? schemaName = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(executor);
    }

    private sealed class CapturingExecutor : IRequestExecutor
    {
        public IOperationRequest? Captured { get; private set; }
        public ulong Version => 1;
        public ISchemaDefinition Schema => null!;
        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task<IExecutionResult> ExecuteAsync(IOperationRequest request, CancellationToken cancellationToken = default)
        {
            Captured = request;
            return Task.FromResult<IExecutionResult>(OperationResult.FromError(ErrorBuilder.New().SetMessage("done").Build()));
        }

        public Task<IResponseStream> ExecuteBatchAsync(OperationRequestBatch requestBatch, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
