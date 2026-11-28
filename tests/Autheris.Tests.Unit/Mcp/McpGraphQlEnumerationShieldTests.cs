namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Autheris.Api.Middleware;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>query_graphql outside Development: unknown and denied nested fields give byte-identical output, so the catalog cannot be enumerated over MCP.</summary>
public sealed class McpGraphQlEnumerationShieldTests
{
    public sealed class Inner
    {
        public string Salary() => throw new GraphQLException(ErrorBuilder.New().SetMessage("Unknown or denied column 'salary' on table 'hr.employees'.").SetCode("INVALID_QUERY").Build());
    }

    public sealed class Query
    {
        public Inner Employees => new();
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

    private static async Task<string> RunAsync(GatewayMcpQueryExecutor executor, string query)
    {
        var tool = new McpToolRegistry(Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), new FixedDemoData()).FindTool("query_graphql")!;
        var session = new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["Analyst"]);
        var args = new JsonObject { ["query"] = query };
        return await executor.ExecuteOperationAsync(tool, args.ToJsonString(), session);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryGraphQl_UnknownVsForbiddenNestedField_ErrorsByteIdentical()
    {
        IHostEnvironment env = new TestHostEnvironment("Production");
        var requests = await new ServiceCollection()
            .AddSingleton(env)
            .AddSingleton(_ => new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance))
            .AddGraphQLServer()
            .AddApplicationService<IHostEnvironment>()
            .AddApplicationService<ErrorSanitizingFilter>()
            .AddErrorFilter(sp => sp.GetRequiredService<ErrorSanitizingFilter>())
            .AddQueryType<Query>()
            .UseRequest<GraphQlEnumerationShieldMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();
        var executor = new GatewayMcpQueryExecutor(new FixedProvider(requests), new StubGatewayExecutionService(),
            NullLogger<GatewayMcpQueryExecutor>.Instance, McpRowCapTests.Options(20),
            datasetCatalog: StubMcpDatasetCatalog.Empty, persistedToolValidator: new PersistedToolValidator());

        var unknown = await RunAsync(executor, "{ employees { nope } }");
        var denied = await RunAsync(executor, "{ employees { salary } }");

        unknown.ShouldBe(denied);
        denied.ShouldNotContain("salary");
        denied.ShouldNotContain("nope");
        denied.ShouldContain("errors");
    }
}
