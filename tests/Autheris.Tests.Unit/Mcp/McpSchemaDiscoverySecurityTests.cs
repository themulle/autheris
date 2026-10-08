namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Directives;
using Autheris.GraphQL.Mcp;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class McpSchemaDiscoverySecurityTests
{
    [Fact]
    public async Task StartAsync_WhenDirectiveHasNoTargetTable_DoesNotGuessTargetTable()
    {
        // MCP-6: @mcpTool without explicit targetTable must NOT guess TargetTable by splitting field names
        // or falling back to default.dbo.fieldName. TargetTable must be null so that
        // AiDataGuardrailService and McpGraphQlTableResolver inspect the actual GraphQL operation AST.
        var schema = SchemaBuilder.New()
            .AddDirectiveType<McpToolDirectiveType>()
            .AddQueryType(d =>
            {
                d.Name("Query");
                d.Field("lookup_customer_orders")
                    .Type<StringType>()
                    .Resolve(_ => "ok")
                    .Directive("mcpTool", new ArgumentNode("name", "lookup_customer_orders"));

                d.Field("explicit_tool")
                    .Type<StringType>()
                    .Resolve(_ => "ok")
                    .Directive("mcpTool",
                        new ArgumentNode("name", "explicit_tool"),
                        new ArgumentNode("targetTable", "sales.crm.leads"));
            })
            .Create();

        var executor = Substitute.For<IRequestExecutor>();
        executor.Schema.Returns(schema);

        var executorProvider = Substitute.For<IRequestExecutorProvider>();
        executorProvider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(executor);

        var toolRegistry = new McpToolRegistry();
        var service = new McpSchemaDiscoveryService(
            executorProvider,
            toolRegistry,
            NullLogger<McpSchemaDiscoveryService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var discoveredLookup = toolRegistry.FindTool("lookup_customer_orders");
        discoveredLookup.ShouldNotBeNull();
        // RED PHASE: Currently this is NOT null because it was guessed as ("lookup", "customer", "orders")
        discoveredLookup.TargetTable.ShouldBeNull();

        var discoveredExplicit = toolRegistry.FindTool("explicit_tool");
        discoveredExplicit.ShouldNotBeNull();
        discoveredExplicit.TargetTable.ShouldBe(new TableIdentifier("sales", "crm", "leads"));
    }
}
