namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class McpHttpTenantTests
{
    private static DefaultHttpContext CreateAuthenticatedContext(
        string? claimTenant,
        SecurityPrincipalContext? securityContext)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "S-1-5-21-ALICE"),
            new(ClaimTypes.PrimarySid, "S-1-5-21-ALICE"),
            new("client_id", "mcp-client")
        };

        if (claimTenant is not null)
        {
            claims.Add(new Claim("tenant_id", claimTenant));
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };

        if (securityContext is not null)
        {
            context.Items[SecurityPrincipalContext.ItemKey] = securityContext;
        }

        return context;
    }

    [Fact]
    public void McpHttp_Explicit_Tenant_In_SecurityContext_Overrides_Claims()
    {
        var securityContext = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ALICE"),
            TenantId = new TenantId("acme"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(["Reader"]),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        var context = CreateAuthenticatedContext(claimTenant: "dflt", securityContext);

        var caller = McpEndpoints.ResolveCaller(context, allowOpenMcp: false);

        caller.TenantId.ShouldBe("acme");
    }

    [Fact]
    public void McpHttp_Malformed_Tenant_Claim_Fails_Closed()
    {
        var context = CreateAuthenticatedContext(claimTenant: "../invalid-tenant", securityContext: null);

        Should.Throw<TenantClaimInvalidException>(() => McpEndpoints.ResolveCaller(context, allowOpenMcp: false));
    }

    [Fact]
    public void McpHttp_Authenticated_Caller_Carries_Resolved_Tenant_And_Identity()
    {
        var securityContext = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ALICE"),
            TenantId = new TenantId("dflt"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(["Developer"]),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        var context = CreateAuthenticatedContext(claimTenant: "dflt", securityContext);

        var caller = McpEndpoints.ResolveCaller(context, allowOpenMcp: false);

        caller.TenantId.ShouldBe("dflt");
        caller.UserSid.ShouldBe("S-1-5-21-ALICE");
        caller.PrincipalId.ShouldBe("mcp-client");
        caller.Roles.ShouldContain("Developer");
    }
}
