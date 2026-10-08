namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Mcp;
using HotChocolate.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class McpQueryExecutorSecurityTests
{
    [Fact]
    public async Task MCP_07_ExecuteOperationAsync_FailsClosed_On_Unexpected_Parsing_Exception()
    {
        var executorProvider = Substitute.For<IRequestExecutorProvider>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();
        var toolValidator = Substitute.For<Autheris.Application.Mcp.Interfaces.IPersistedToolValidator>();
        toolValidator.When(v => v.ValidateToolInvocation(Arg.Any<McpToolDefinition>(), Arg.Any<System.Text.Json.JsonElement>(), out Arg.Any<string?>()))
            .Do(_ => throw new InvalidOperationException("Simulated validator crash"));

        var mcpExecutor = new GatewayMcpQueryExecutor(
            executorProvider,
            gatewayExec,
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            persistedToolValidator: toolValidator);

        var tool = new McpToolDefinition("query_orders", "Query orders", "{}", "");
        var session = new McpSessionContext("s1", "spn-1", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Roles: ["Reader"]);

        var json = await mcpExecutor.ExecuteOperationAsync(tool, "{\"query\":\"test\"}", session);

        json.ShouldContain("isError\":true");
        json.ShouldContain("Invalid arguments JSON payload.");
    }

    [Fact]
    public async Task MCP_04_FastPath_Does_Not_Synthesize_Reader_Or_AiAgent_Roles_In_Principal()
    {
        var executorProvider = Substitute.For<IRequestExecutorProvider>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();

        ClaimsPrincipal? capturedPrincipal = null;
        gatewayExec.ExecuteTableQueryAsync(
            Arg.Do((ClaimsPrincipal p) => capturedPrincipal = p),
            Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult((
                (IReadOnlyList<IReadOnlyDictionary<string, object?>>)new List<IReadOnlyDictionary<string, object?>>(),
                TableAccessDecision.Allowed(new TableIdentifier("finance", "dbo", "customers"), new Dictionary<string, ColumnAccessLevel>())
            )));

        var mcpExecutor = new GatewayMcpQueryExecutor(
            executorProvider,
            gatewayExec,
            NullLogger<GatewayMcpQueryExecutor>.Instance);

        var tool = new McpToolDefinition("query_customers", "Query customers", "{}", "");
        var session = new McpSessionContext("s1", "spn-1", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Roles: null);

        await mcpExecutor.ExecuteOperationAsync(tool, "{}", session);

        capturedPrincipal.ShouldNotBeNull();
        capturedPrincipal.FindAll(ClaimTypes.Role).Select(c => c.Value).ShouldBeEmpty();
    }
}
