namespace Autheris.Tests.Unit.Tenancy;

using Autheris.Application.Identity;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Tenancy;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>DT-A-9 / WP-DT-7: the MCP stdio session has no built-in tenant: explicit --tenant, else D, else it refuses to start.</summary>
[Trait("Category", "Security")]
public sealed class McpStdioTenantTests
{
    private static (McpStdioRunner Runner, IMcpProtocolHandler Handler) Build(string? defaultTenant)
    {
        var handler = Substitute.For<IMcpProtocolHandler>();
        handler.CreateSession(Arg.Any<string>(), Arg.Any<string>()).ReturnsForAnyArgs(ci =>
            new McpSessionContext("s1", (string)ci[0], (string)ci[1], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var tenancy = new TenancyOptions { DefaultTenantId = defaultTenant };
        var resolver = new TenantResolver(() => tenancy, Substitute.For<IIdentityRealmRegistry>(), NullLogger<TenantResolver>.Instance);
        return (new McpStdioRunner(handler, null, resolver), handler);
    }

    [Fact]
    public async Task DT_A_9_Stdio_Refuses_To_Start_Without_Tenant_And_Without_Default_Tenant()
    {
        var (runner, handler) = Build(defaultTenant: null);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            runner.RunAsync(new StringReader(string.Empty), new StringWriter()));

        handler.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task DT_A_9_Stdio_Uses_The_Default_Tenant_When_No_Tenant_Is_Passed()
    {
        var (runner, handler) = Build("dflt");

        await runner.RunAsync(new StringReader(string.Empty), new StringWriter());

        handler.Received(1).CreateSession(Arg.Any<string>(), "dflt");
    }

    [Fact]
    public async Task DT_A_9_Stdio_Explicit_Tenant_Wins_Over_The_Default_Tenant()
    {
        var (runner, handler) = Build("dflt");

        await runner.RunAsync(new StringReader(string.Empty), new StringWriter(), tenantId: "acme");

        handler.Received(1).CreateSession(Arg.Any<string>(), "acme");
    }
}
