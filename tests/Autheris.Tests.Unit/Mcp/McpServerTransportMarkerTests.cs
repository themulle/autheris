namespace Autheris.Tests.Unit.Mcp;

using System.Security.Claims;
using Autheris.Api.Mcp;
using Autheris.Application.Mcp;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>The guarded tool call marks the transport HttpContext for exactly the duration of the guardrail call.</summary>
[Trait("Category", "Security")]
public sealed class McpServerTransportMarkerTests
{
    private const string Tenant = "00000000-0000-0000-0000-000000000001";

    private sealed class ObservingGuardrail(HttpContext http, Action<bool> observe, Exception? toThrow) : IAiDataGuardrailService
    {
        public ValueTask<McpToolCallResult> ExecuteToolWithGuardrailAsync(McpToolCallRequest request, McpSessionContext sessionContext, CancellationToken cancellationToken = default)
        {
            observe(http.Items.TryGetValue(McpTransportItems.ToolCallKey, out var v) && v is true);
            return toThrow is null ? ValueTask.FromResult(new McpToolCallResult(true, "{}")) : throw toThrow;
        }
    }

    private static async Task<(bool During, bool After)> RunAsync(Exception? toThrow)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", Tenant)], "Bearer"))
        };
        http.Items[Autheris.Domain.Security.SecurityPrincipalContext.TenantIdItemKey] = new TenantId(Tenant);
        var during = false;
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = http });
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new GatewayOptions()));
        services.AddSingleton<IAiDataGuardrailService>(new ObservingGuardrail(http, v => during = v, toThrow));
        await using var sp = services.BuildServiceProvider();

        await GatewayMcpServer.CallGuardedToolAsync(sp, "query_graphql", "{}", default);

        return (during, http.Items.ContainsKey(McpTransportItems.ToolCallKey));
    }

    [Fact]
    public async Task GuardedToolCall_MarksTransport_DuringCallOnly()
    {
        var (during, after) = await RunAsync(null);

        during.ShouldBeTrue();
        after.ShouldBeFalse();
    }

    [Fact]
    public async Task GuardedToolCall_RemovesMarker_WhenGuardrailThrows()
    {
        var (during, after) = await RunAsync(new InvalidOperationException("boom"));

        during.ShouldBeTrue();
        after.ShouldBeFalse();
    }
}
