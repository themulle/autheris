namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Security.Claims;
using System;
using System.Linq;
using Autheris.Api.Endpoints;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>SEC-OI-508-6: the claim order of a token is IdP-controlled, so a multi-valued scalar attribute is ambiguous, never first-wins.</summary>
[Trait("Category", "Security")]
public sealed class McpCallerTests
{
    private static McpEndpoints.McpCaller Resolve(params Claim[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001"), new("tenant_id", "tenant_a") };
        claims.AddRange(extra);
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        return McpEndpoints.ResolveCaller(ctx, allowOpenMcp: false);
    }

    [Fact]
    public void Multi_valued_claim_is_ambiguous_not_first()
    {
        var caller = Resolve(new Claim("department", "public"), new Claim("department", "finance"), new Claim("region", "eu"));

        caller.AdditionalClaims.ShouldNotBeNull();
        caller.AdditionalClaims.ShouldNotContainKey("department");
        caller.AdditionalClaims["region"].ShouldBe("eu");
    }

    [Fact]
    public void Claim_order_does_not_change_the_outcome()
    {
        var a = Resolve(new Claim("department", "public"), new Claim("department", "finance"));
        var b = Resolve(new Claim("department", "finance"), new Claim("department", "public"));

        a.AdditionalClaims.ShouldNotBeNull();
        b.AdditionalClaims.ShouldNotBeNull();
        a.AdditionalClaims.ShouldNotContainKey("department");
        b.AdditionalClaims.ShouldNotContainKey("department");
    }

    [Fact]
    public void Single_valued_claim_is_kept()
    {
        var caller = Resolve(new Claim("department", "finance"));
        caller.AdditionalClaims.ShouldNotBeNull();
        caller.AdditionalClaims["department"].ShouldBe("finance");
    }

    [Fact]
    public void Multiple_scp_claims_are_merged_not_dropped()
    {
        var caller = Resolve(new Claim("scp", "Agent.Read data.access.request"), new Claim("scp", "Sql.Write"), new Claim("scp", "Agent.Read"));

        caller.AdditionalClaims.ShouldNotBeNull();
        caller.AdditionalClaims["scp"].Split(' ').ShouldBe(["Agent.Read", "data.access.request", "Sql.Write"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("scp")]
    [InlineData("http://schemas.microsoft.com/identity/claims/scope")]
    [InlineData("scope")]
    public void Every_scope_claim_type_keeps_all_values(string type)
    {
        var caller = Resolve(new Claim(type, "a.one"), new Claim(type, "b.two c.three"));

        caller.AdditionalClaims.ShouldNotBeNull();
        caller.AdditionalClaims[type].Split(' ').ShouldBe(["a.one", "b.two", "c.three"], ignoreOrder: true);
    }

    [Fact]
    public void Merged_scopes_reach_session_principal_token_scopes()
    {
        var caller = Resolve(new Claim("scp", "Agent.Read"), new Claim("scp", "Other.Scope"));
        var session = new McpSessionContext("s1", "client", "tenant_a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            UserSid: caller.UserSid, AdditionalClaims: caller.AdditionalClaims);

        var principal = McpProtocolHandler.BuildPrincipalFromSession(session);

        var scopes = TokenAccessScope.GetTokenScopes(principal);
        scopes.ShouldContain("Agent.Read");
        scopes.ShouldContain("Other.Scope");
    }

    [Fact]
    public void Conflicting_non_scope_claim_is_still_dropped()
    {
        var caller = Resolve(new Claim("scp", "a.one"), new Claim("scp", "b.two"), new Claim("department", "x"), new Claim("department", "y"));

        caller.AdditionalClaims.ShouldNotBeNull();
        caller.AdditionalClaims.ShouldNotContainKey("department");
        caller.AdditionalClaims.ShouldContainKey("scp");
    }
}
