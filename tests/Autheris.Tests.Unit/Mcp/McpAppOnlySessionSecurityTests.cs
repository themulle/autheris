namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Catalog.External;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.GraphQL.Security;
using Autheris.Tests.Unit.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class McpAppOnlySessionSecurityTests
{
    private const string TrustedTenantGuid = "11111111-2222-3333-4444-555555555555";
    private const string TrustedSpClientId = "sp-client-approved-1";
    private const string UntrustedSpClientId = "sp-client-untrusted-99";

    private static IdentityRealm WorkforceRealm(string? allowedClientId = null) =>
        new(
            id: "entra-workforce",
            kind: IdentityRealmKind.EntraWorkforce,
            tenantBinding: TenantBindingSpecification.Fixed(new TenantId("t1")),
            issuers: [$"https://login.microsoftonline.com/{TrustedTenantGuid}/v2.0"],
            audiences: ["api://autheris-gw"],
            allowedClientIds: allowedClientId is null ? null : [allowedClientId]);

    private static McpSessionContext AppSession(string spClientId, Dictionary<string, string>? additionalClaims = null) =>
        new(
            SessionId: "sess-app-1",
            ServicePrincipalId: spClientId,
            TenantId: "t1",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow.AddMinutes(5),
            UserSid: null,
            Roles: ["Consumer"],
            GroupSids: ["S-1-5-21-1-2-3-5001"],
            AdditionalClaims: additionalClaims);

    [Fact]
    public void AppOnlySession_WithServicePrincipalIdEqualSid_EmitsClientIdAzpAndAppMarkers_AcrossMcpAndGraphQl()
    {
        var session = AppSession(UntrustedSpClientId);

        foreach (var principal in new[]
        {
            McpProtocolHandler.BuildPrincipalFromSession(session)!,
            GraphQlCallerPrincipal.BuildMcpSessionPrincipal(session)
        })
        {
            principal.GetUserSid().ShouldBe(new Sid(UntrustedSpClientId));
            principal.FindFirst("client_id")?.Value.ShouldBe(UntrustedSpClientId);
            principal.FindFirst("azp")?.Value.ShouldBe(UntrustedSpClientId);
            principal.FindFirst("idtyp")?.Value.ShouldBe("app");
            principal.HasClaim(McpProtocolHandler.IdentityKindClaimType, McpProtocolHandler.ApplicationIdentityKind).ShouldBeTrue();
            principal.GetGroupSids().ShouldBeEmpty();
            AppOnlyPrincipal.Is(principal).ShouldBeTrue();
            ExternalApiPermissions.IsServicePrincipal(principal).ShouldBeTrue();
        }
    }

    [Fact]
    public void AppOnlySession_UntrustedClient_WithRequireMemberIndicatorFalse_NeverFallsOpenAsHumanWorkforceUser()
    {
        var session = AppSession(
            UntrustedSpClientId,
            new Dictionary<string, string>
            {
                ["tid"] = TrustedTenantGuid,
                ["iss"] = $"https://login.microsoftonline.com/{TrustedTenantGuid}/v2.0",
                ["aud"] = "api://autheris-gw"
            });

        var policy = new InternalAudiencePolicy(
            TrustedIssuerTenantIds: [TrustedTenantGuid],
            EmployeeGroupSids: [],
            RequireMemberIndicator: false,
            TrustedServicePrincipalClientIds: [TrustedSpClientId]);

        foreach (var principal in new[]
        {
            McpProtocolHandler.BuildPrincipalFromSession(session)!,
            GraphQlCallerPrincipal.BuildMcpSessionPrincipal(session)
        })
        {
            var subject = SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: new TenantId("t1"));
            InternalAudienceResolver.IsInternal(subject, policy, realm: WorkforceRealm()).ShouldBeFalse();
        }
    }

    [Fact]
    public void AppOnlySession_TrustedClient_WithServicePrincipalIdEqualSid_SatisfiesInternalAudience()
    {
        var session = AppSession(
            TrustedSpClientId,
            new Dictionary<string, string>
            {
                ["tid"] = TrustedTenantGuid,
                ["iss"] = $"https://login.microsoftonline.com/{TrustedTenantGuid}/v2.0",
                ["aud"] = "api://autheris-gw"
            });

        var policy = new InternalAudiencePolicy(
            TrustedIssuerTenantIds: [TrustedTenantGuid],
            EmployeeGroupSids: [],
            RequireMemberIndicator: true,
            TrustedServicePrincipalClientIds: [TrustedSpClientId]);

        foreach (var principal in new[]
        {
            McpProtocolHandler.BuildPrincipalFromSession(session)!,
            GraphQlCallerPrincipal.BuildMcpSessionPrincipal(session)
        })
        {
            var subject = SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: new TenantId("t1"));
            InternalAudienceResolver.IsInternal(subject, policy, realm: WorkforceRealm(TrustedSpClientId)).ShouldBeTrue();
        }
    }

    [Fact]
    public void McpEndpoints_ResolveCaller_AppOnlyVariants_PreserveClientIdAndClearUserSidAndGroups()
    {
        var sameSidHttp = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "sp-same-sid"),
                    new Claim("client_id", "sp-same-sid"),
                    new Claim("tenant_id", "t1"),
                    IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-5001")
                ],
                "Bearer"))
        };
        var sameSidCaller = McpEndpoints.ResolveCaller(sameSidHttp, allowOpenMcp: false);
        sameSidCaller.UserSid.ShouldBeNull();
        sameSidCaller.PrincipalId.ShouldBe("sp-same-sid");
        sameSidCaller.GroupSids.ShouldBeEmpty();

        var clientCredsHttp = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("oid", "sp-object-guid"),
                    new Claim("azp", "sp-client-guid"),
                    new Claim("gty", "client-credentials"),
                    new Claim("tenant_id", "t1"),
                    IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-5001")
                ],
                "Bearer"))
        };
        var clientCredsCaller = McpEndpoints.ResolveCaller(clientCredsHttp, allowOpenMcp: false);
        clientCredsCaller.UserSid.ShouldBeNull();
        clientCredsCaller.PrincipalId.ShouldBe("sp-client-guid");
        clientCredsCaller.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task HumanSession_And_Pipeline_RejectSpoofedAppOnlyIdentityClaims()
    {
        var userSession = new McpSessionContext(
            SessionId: "sess-user-1",
            ServicePrincipalId: "S-1-5-21-ALICE",
            TenantId: "t1",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow.AddMinutes(5),
            UserSid: "S-1-5-21-ALICE",
            Roles: ["Consumer"],
            GroupSids: [],
            AdditionalClaims: new Dictionary<string, string>
            {
                ["idtyp"] = "app",
                [McpProtocolHandler.IdentityKindClaimType] = McpProtocolHandler.ApplicationIdentityKind,
                ["autheris:subject_kind"] = "sp",
                ["department"] = "engineering"
            });

        foreach (var principal in new[]
        {
            McpProtocolHandler.BuildPrincipalFromSession(userSession)!,
            GraphQlCallerPrincipal.BuildMcpSessionPrincipal(userSession)
        })
        {
            principal.FindFirst("idtyp").ShouldBeNull();
            principal.FindFirst(McpProtocolHandler.IdentityKindClaimType).ShouldBeNull();
            principal.FindFirst("autheris:subject_kind").ShouldBeNull();
            principal.FindFirst("client_id").ShouldBeNull();
            principal.FindFirst("azp").ShouldBeNull();
            principal.FindFirst("department")?.Value.ShouldBe("engineering");
            AppOnlyPrincipal.Is(principal).ShouldBeFalse();
        }

        var pipeline = TestIdentityPipeline.Create(TestIdentityPipeline.TokenRealm());
        var raw = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "user-1"),
                new Claim("oid", "user-1"),
                new Claim("upn", "alice@corp.example"),
                new Claim(McpProtocolHandler.IdentityKindClaimType, McpProtocolHandler.ApplicationIdentityKind)
            ],
            "Bearer"));

        var processed = await pipeline.ProcessAsync(raw, "Bearer", null, CancellationToken.None);
        processed.FindFirst(McpProtocolHandler.IdentityKindClaimType).ShouldBeNull();
        AppOnlyPrincipal.Is(processed).ShouldBeFalse();
    }
}
