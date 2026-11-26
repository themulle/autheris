namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GatewayMcpQueryExecutorMaskingTests
{
    public sealed class MaskingQuery
    {
        public string MaskedField(IResolverContext ctx)
        {
            if (ctx.ContextData.TryGetValue(MaskedFieldsMiddleware.ContextKey, out var bag) && bag is ConcurrentBag<string> masked)
            {
                masked.Add(ctx.Path.Print());
            }

            return "[REDACTED]";
        }

        public string ClearField() => "cleartext";

        public string? BrokenField() => throw new InvalidOperationException("broken");
    }

    private static McpSessionContext Session() =>
        new("sess-1181", "agent-principal", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static async Task<GatewayMcpQueryExecutor> CreateAsync(StubGatewayExecutionService? gateway = null)
    {
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<MaskingQuery>()
            .UseRequest<MaskedFieldsMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        return new GatewayMcpQueryExecutor(
            new FixedExecutorProvider(executor),
            gateway ?? new StubGatewayExecutionService(),
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            new PersistedToolValidator());
    }

    private static McpToolDefinition GraphQlTool() =>
        McpDatasetTools.Definitions().ToList().Find(t => t.Name == McpDatasetTools.QueryGraphQl) ?? throw new InvalidOperationException();

    private static async Task<bool?> RunGraphQlAsync(string query)
    {
        var executor = await CreateAsync();
        using var scope = McpResultMasking.Begin();
        await executor.ExecuteOperationAsync(GraphQlTool(), System.Text.Json.JsonSerializer.Serialize(new { query }), Session());
        return scope.Outcome;
    }

    [Fact]
    public void HasMaskedFields_Null_False() =>
        GatewayMcpQueryExecutor.HasMaskedFields(null).ShouldBeFalse();

    [Fact]
    public void HasMaskedFields_Absent_False() =>
        GatewayMcpQueryExecutor.HasMaskedFields(new Dictionary<string, object?> { ["cost"] = 1 }).ShouldBeFalse();

    [Fact]
    public void HasMaskedFields_Present_True() =>
        GatewayMcpQueryExecutor.HasMaskedFields(new Dictionary<string, object?> { [MaskedFieldsMiddleware.ExtensionKey] = new[] { "a" } }).ShouldBeTrue();

    [Fact]
    public void ExtensionKey_IsTheWireName() =>
        MaskedFieldsMiddleware.ExtensionKey.ShouldBe("maskedFields");

    [Fact]
    public async Task GraphQlResult_WithMaskedFields_Reports() =>
        (await RunGraphQlAsync("{ maskedField }")).ShouldBe(true);

    [Fact]
    public async Task GraphQlResult_WithoutMaskedFields_ReportsFalse() =>
        (await RunGraphQlAsync("{ clearField }")).ShouldBe(false);

    [Fact]
    public async Task GraphQlError_WithMaskedFields_Reports() =>
        (await RunGraphQlAsync("{ maskedField brokenField }")).ShouldBe(true);

    [Fact]
    public async Task GraphQlError_WithoutMaskedFields_ReportsFalse() =>
        (await RunGraphQlAsync("{ brokenField }")).ShouldBe(false);

    private static StubGatewayExecutionService Gateway(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, params (string Column, ColumnAccessLevel Level)[] access)
    {
        var table = new TableIdentifier("sales", "dbo", "customers");
        var decision = new TableAccessDecision(table, true, access.ToDictionaryOrdinal(), null, []);
        return new StubGatewayExecutionService
        {
            OnExecuteQuery = (_, _, _, _, _, _, _, _) => Task.FromResult((rows, decision))
        };
    }

    private static async Task<bool?> RunFastPathAsync(StubGatewayExecutionService gateway)
    {
        var executor = await CreateAsync(gateway);
        var tool = new McpToolRegistry().FindTool("query_customers") ?? throw new InvalidOperationException();
        using var scope = McpResultMasking.Begin();
        await executor.ExecuteOperationAsync(tool, "{}", Session());
        return scope.Outcome;
    }

    [Fact]
    public async Task FastPath_MaskedColumn_Reports()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["id"] = 1, ["ssn"] = "***" } };
        (await RunFastPathAsync(Gateway(rows, ("id", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Mask)))).ShouldBe(true);
    }

    [Fact]
    public async Task FastPath_ClearColumns_ReportsFalse()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["id"] = 1 } };
        (await RunFastPathAsync(Gateway(rows, ("id", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Mask)))).ShouldBe(false);
    }

    private sealed class FixedExecutorProvider(IRequestExecutor executor) : IRequestExecutorProvider
    {
        public ImmutableArray<string> SchemaNames { get; } = [ISchemaDefinition.DefaultName];

        public ValueTask<IRequestExecutor> GetExecutorAsync(string? schemaName = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(executor);
    }
}

file static class DecisionExtensions
{
    public static Dictionary<string, ColumnAccessLevel> ToDictionaryOrdinal(this (string Column, ColumnAccessLevel Level)[] access)
    {
        var dict = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, level) in access)
        {
            dict[column] = level;
        }

        return dict;
    }
}
