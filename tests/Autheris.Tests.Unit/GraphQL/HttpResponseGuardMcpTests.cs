namespace Autheris.Tests.Unit.GraphQL;

using Autheris.Application.Mcp;
using Autheris.GraphQL.Interceptors;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>An in-process GraphQL execution for an MCP tool call must leave the MCP transport response untouched.</summary>
public sealed class HttpResponseGuardMcpTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void HttpResponseGuard_McpToolCall_LeavesStatusAndHeadersUntouched()
    {
        var marked = new DefaultHttpContext();
        marked.Items[McpTransportItems.ToolCallKey] = true;
        var plain = new DefaultHttpContext();

        foreach (var context in new[] { marked, plain })
        {
            HttpResponseGuard.SetStatus(context, StatusCodes.Status429TooManyRequests);
            HttpResponseGuard.SetHeader(context, "X-Query-Cost", "9");
        }

        marked.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        marked.Response.Headers.ContainsKey("X-Query-Cost").ShouldBeFalse();
        plain.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        plain.Response.Headers["X-Query-Cost"].ToString().ShouldBe("9");
    }
}
