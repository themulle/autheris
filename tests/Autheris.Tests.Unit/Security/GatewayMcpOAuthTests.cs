namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using Autheris.Api.Mcp;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public sealed class GatewayMcpOAuthTests
{
    [Fact]
    public void IsEnabled_WhenMcpDisabled_ReturnsFalse()
    {
        var options = new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = false },
            Authentication = new AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions { Enabled = true, TenantId = "11111111-1111-1111-1111-111111111111" }
            }
        };

        GatewayMcpOAuth.IsEnabled(options).ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_WhenEntraEnabledWithoutTenantId_ReturnsFalse()
    {
        // SR15-29: EntraId is enabled but TenantId is empty, so AuthorizationServers count is 0.
        // IsEnabled must return false to avoid mapping MCP without registered MCP OAuth service.
        var options = new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true },
            Authentication = new AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions { Enabled = true, TenantId = "" },
                Adfs = new AdfsAuthOptions { Enabled = false }
            }
        };

        GatewayMcpOAuth.IsEnabled(options).ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_WhenEntraEnabledWithValidTenantId_ReturnsTrue()
    {
        var options = new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true },
            Authentication = new AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions { Enabled = true, TenantId = "11111111-1111-1111-1111-111111111111" }
            }
        };

        GatewayMcpOAuth.IsEnabled(options).ShouldBeTrue();
    }

    [Fact]
    public void Scopes_AdvertisesAgentReadInsteadOfDefault()
    {
        // SR15-39: Must advertise Agent.Read instead of <aud>/.default
        var options = new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true },
            Authentication = new AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions
                {
                    Enabled = true,
                    TenantId = "11111111-1111-1111-1111-111111111111",
                    Audience = "api://autheris"
                }
            }
        };

        var scopes = GatewayMcpOAuth.GetSupportedScopes(options);
        scopes.ShouldContain("Agent.Read");
        scopes.ShouldContain("api://autheris/Agent.Read");
        scopes.ShouldNotContain("api://autheris/.default");
    }
}
