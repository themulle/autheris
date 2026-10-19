namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Security.Claims;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;
using static CallerAttributeTestSupport;

/// <summary>
/// ST-129-01 (BC-129-01, F-6): the MCP session identity is rebuilt by the gateway as <c>McpAuth</c> and loses the original scheme. Only a gateway
/// marker, set when the original scheme was allowed, lets the rebuilt identity pass the scheme gate (marker is never taken from a client).
/// </summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeMcpSchemeGateTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    private static McpSessionContext SessionOf(Autheris.Api.Endpoints.McpEndpoints.McpCaller caller)
    {
        var now = DateTimeOffset.UtcNow;
        return new McpSessionContext("s", caller.PrincipalId, caller.TenantId, now, now, caller.UserSid, caller.Roles, caller.GroupSids,
            caller.ClientIp, caller.IsReadOnly, caller.AdditionalClaims);
    }

    private static CallerSubjectKind KindThroughMcp(string scheme, params Claim[] extra)
    {
        var claims = new System.Collections.Generic.List<Claim> { new(ClaimTypes.PrimarySid, Sid), new("tenant_id", "tenant_a"), new("upn", "a@example.com") };
        claims.AddRange(extra);
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, scheme)) };
        var caller = Autheris.Api.Endpoints.McpEndpoints.ResolveCaller(ctx, allowOpenMcp: false, new CallerAttributeOptions());
        var rebuilt = Autheris.GraphQL.Mcp.GatewayMcpQueryExecutor.BuildPrincipal(SessionOf(caller));
        return CallerSubjectFactory.Create(rebuilt, Tenant, Options(true)).Kind;
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("ForwardAuth")]
    [InlineData("TestAuth")]
    public void ST_129_01_McpSession_OfANonAllowedScheme_IsNone(string scheme) => KindThroughMcp(scheme).ShouldBe(CallerSubjectKind.None);

    [Fact]
    public void ST_129_01_McpSession_OfABearerIdentity_IsUser() => KindThroughMcp("Bearer").ShouldBe(CallerSubjectKind.User);

    [Fact]
    public void ST_129_01_ClientSuppliedMarkerClaim_IsStripped_AndNeverHonoured() =>
        KindThroughMcp("Basic", new Claim(CallerAttributeSchemeGate.MarkerClaimType, "1")).ShouldBe(CallerSubjectKind.None);

    [Fact]
    public void ST_129_01_Marker_IsOnlyHonoured_OnAnMcpAuthIdentity()
    {
        var basic = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, Sid), new Claim(CallerAttributeSchemeGate.MarkerClaimType, "1")], "Basic"));
        CallerSubjectFactory.Create(basic, Tenant, Options(true)).Kind.ShouldBe(CallerSubjectKind.None);
        var mcpNoMarker = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, Sid)], "McpAuth"));
        CallerSubjectFactory.Create(mcpNoMarker, Tenant, Options(true)).Kind.ShouldBe(CallerSubjectKind.None);
    }

    [Fact]
    public void ST_129_01_DefaultAllowedSchemes_DoNotListMcpAuth() =>
        new CallerAttributeClaimsOptions().AllowedSchemes.ShouldNotContain("McpAuth");
}
