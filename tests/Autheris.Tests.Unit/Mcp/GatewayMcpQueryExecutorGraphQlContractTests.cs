namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common.Fakes;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>query_graphql returns the POST /graphql body: no row cap, no document rewriting, errors from the executor result verbatim.</summary>
public sealed class GatewayMcpQueryExecutorGraphQlContractTests
{
    private const int Cap = 20;
    private const int Produced = 150;

    public sealed class Row
    {
        public int Id { get; set; }
    }

    public sealed class Query
    {
        public List<Row> Orders => Enumerable.Range(0, Produced).Select(i => new Row { Id = i }).ToList();

        public int Fine => 1;

        public string? Partial => throw new GraphQLException(ErrorBuilder.New().SetMessage("partial failed").SetCode("PARTIAL_FAILED").Build());

        [GraphQLNonNullType]
        public string? Boom => throw new GraphQLException(ErrorBuilder.New().SetMessage("boom failed").SetCode("BOOM_FAILED").Build());

        public string? Echo(string? text) => text;

        public int? EchoInt(int? n) => n;
    }

    private sealed class FixedProvider(IRequestExecutor executor) : IRequestExecutorProvider
    {
        public ImmutableArray<string> SchemaNames { get; } = [ISchemaDefinition.DefaultName];

        public ValueTask<IRequestExecutor> GetExecutorAsync(string? schemaName = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(executor);
    }

    private sealed class FixedDemoData : IDemoDataSwitch
    {
        public bool Enabled => false;
    }

    private sealed record Rig(GatewayMcpQueryExecutor Executor, IRequestExecutor Requests, Func<IOperationRequest?> Captured);

    private static async Task<Rig> CreateAsync()
    {
        IOperationRequest? captured = null;
        var requests = await new ServiceCollection().AddGraphQL().AddQueryType<Query>()
            .UseRequest(next => async ctx =>
            {
                captured = ctx.Request;
                await next(ctx).ConfigureAwait(false);
            })
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();
        var executor = new GatewayMcpQueryExecutor(new FixedProvider(requests), new StubGatewayExecutionService(),
            NullLogger<GatewayMcpQueryExecutor>.Instance, McpRowCapTests.Options(Cap),
            datasetCatalog: StubMcpDatasetCatalog.Empty, persistedToolValidator: new PersistedToolValidator());
        return new Rig(executor, requests, () => captured);
    }

    private static McpToolDefinition Tool() =>
        new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData()).FindTool("query_graphql")
        ?? throw new InvalidOperationException("query_graphql");

    private static McpSessionContext Session() =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["Analyst"]);

    private static async Task<JsonObject> RunAsync(Rig rig, string query, string? variablesJson = null)
    {
        var args = new JsonObject { ["query"] = query };
        if (variablesJson is not null)
        {
            args["variables"] = JsonNode.Parse(variablesJson);
        }

        var json = await rig.Executor.ExecuteOperationAsync(Tool(), args.ToJsonString(), Session());
        return JsonNode.Parse(json)!.AsObject();
    }

    private static OperationDefinitionNode CapturedOperation(Rig rig)
    {
        var document = rig.Captured().ShouldNotBeNull().Document.ShouldNotBeNull().ShouldBeAssignableTo<IOperationDocumentNodeProvider>().Document;
        return document!.Definitions.OfType<OperationDefinitionNode>().Single();
    }

    [Fact]
    public async Task QueryGraphQl_PartialData_IsSuccessWithBodyVerbatim()
    {
        var rig = await CreateAsync();

        var root = await RunAsync(rig, "{ fine partial }");

        root.ContainsKey("error").ShouldBeFalse();
        root["data"]!["fine"]!.GetValue<int>().ShouldBe(1);
        root["data"]!["partial"].ShouldBeNull();
        root["errors"]!.AsArray().Single()!["extensions"]!["code"]!.GetValue<string>().ShouldBe("PARTIAL_FAILED");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryGraphQl_ErrorsWithoutData_EnvelopeWithVerbatimErrors()
    {
        var rig = await CreateAsync();

        var root = await RunAsync(rig, "{ boom }");

        root.Select(p => p.Key).ShouldBe(["error", "errors"]);
        root["error"]!["code"]!.GetValue<string>().ShouldBe("BOOM_FAILED");
        root["errors"]!.AsArray().Single()!["message"]!.GetValue<string>().ShouldBe("boom failed");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryGraphQl_ErrorsComeOnlyFromOperationResult()
    {
        var rig = await CreateAsync();

        var root = await RunAsync(rig, "{ boom }");

        var direct = (OperationResult)await rig.Requests.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ boom }").Build());
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        HotChocolate.Transport.Formatters.JsonResultFormatter.Default.Format(direct, buffer);
        var expected = JsonNode.Parse(buffer.WrittenSpan)!["errors"];
        JsonNode.DeepEquals(root["errors"], expected).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryGraphQl_ListLongerThanCap_NotTrimmed()
    {
        var rig = await CreateAsync();

        var root = await RunAsync(rig, "{ orders { id } }");

        root["data"]!["orders"]!.AsArray().Count.ShouldBe(Produced);
        root.ContainsKey("truncated").ShouldBeFalse();
        root.ContainsKey("rowCap").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryGraphQl_DocumentReachesExecutorUnmodified()
    {
        var rig = await CreateAsync();

        await RunAsync(rig, "query Q($n: Int) { echoInt(n: $n) }", """{"n":5}""");

        CapturedOperation(rig).VariableDefinitions.Single().DefaultValue.ShouldBeNull();
    }

    [Fact]
    public async Task QueryGraphQl_Variables_DocumentNotRewritten()
    {
        var rig = await CreateAsync();

        var root = await RunAsync(rig, "query Q($n: Int) { echoInt(n: $n) }", """{"n":7}""");

        root["data"]!["echoInt"]!.GetValue<int>().ShouldBe(7);
        CapturedOperation(rig).VariableDefinitions.Single().DefaultValue.ShouldBeNull();
    }

    [Fact]
    public async Task QueryGraphQl_StringVariableWithNumericValue_NotRewritten()
    {
        var rig = await CreateAsync();

        var root = await RunAsync(rig, "query Q($t: String) { echo(text: $t) }", """{"t":"123"}""");

        root["data"]!["echo"]!.GetValue<string>().ShouldBe("123");
        CapturedOperation(rig).VariableDefinitions.Single().DefaultValue.ShouldBeNull();
    }
}
