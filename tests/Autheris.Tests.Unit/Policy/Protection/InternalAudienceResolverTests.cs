namespace Autheris.Tests.Unit.Policy.Protection;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Identity;
using Autheris.Application.Policy.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class InternalAudienceResolverTests
{
    private const string TrustedTenantUpper = "11111111-2222-3333-4444-555555555555";
    private const string ForeignTenantGuid = "99999999-aaaa-bbbb-cccc-dddddddddddd";
    private const string EmpGroupSid = "S-1-5-21-EMP-100";
    private const string TrustedAppId = "sp-client-approved-1";

    private static InternalAudiencePolicy DefaultPolicy(
        bool requireMemberIndicator = true,
        string[]? trustedTenants = null,
        string[]? empGroups = null,
        string[]? trustedSpClients = null) =>
        new(
            TrustedIssuerTenantIds: trustedTenants ?? [TrustedTenantUpper.ToUpperInvariant()],
            EmployeeGroupSids: empGroups ?? [EmpGroupSid],
            RequireMemberIndicator: requireMemberIndicator,
            TrustedServicePrincipalClientIds: trustedSpClients ?? [TrustedAppId]);

    private static IdentityRealm WorkforceRealm(string? allowedClient = TrustedAppId) =>
        new(
            id: "entra-workforce",
            kind: IdentityRealmKind.EntraWorkforce,
            tenantBinding: TenantBindingSpecification.Fixed(new TenantId("t1")),
            issuers: [$"https://login.microsoftonline.com/{TrustedTenantUpper}/v2.0"],
            audiences: ["api://autheris-gw"],
            allowedClientIds: allowedClient is null ? null : [allowedClient]);

    private static SecurityPrincipalContext BuildSubject(
        IEnumerable<Claim> claims,
        IdentityRealmKind? realmKind = IdentityRealmKind.EntraWorkforce,
        GroupResolutionStatus groupStatus = GroupResolutionStatus.Complete,
        DateTimeOffset? groupsExpireAt = null,
        string[]? groups = null,
        bool isAuthenticated = true) =>
        new()
        {
            UserSid = new Sid("S-1-5-21-USER-1"),
            ResolvedTenant = new TenantId("t1"),
            GroupSids = new HashSet<Sid>((groups ?? []).Select(g => new Sid(g))),
            GroupResolutionStatus = groupStatus,
            GroupsExpireAt = groupsExpireAt,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = isAuthenticated,
            RealmKind = realmKind,
            Claims = [.. claims]
        };

    private static IEnumerable<Claim> BaseWorkforceClaims(string tid = TrustedTenantUpper, string? issTid = null) =>
    [
        new("tid", tid),
        new("iss", $"https://login.microsoftonline.com/{issTid ?? TrustedTenantUpper}/v2.0"),
        new("aud", "api://autheris-gw")
    ];

    [Theory]
    [InlineData("acct", "1")]
    [InlineData("idp", $"https://login.microsoftonline.com/{ForeignTenantGuid}/v2.0")]
    [InlineData("upn", "guest_partner.com#EXT#@contoso.onmicrosoft.com")]
    [InlineData("preferred_username", "alice_ext.io#EXT#@tenant.onmicrosoft.com")]
    public void Guest_not_internal(string guestClaimType, string guestClaimValue)
    {
        var claims = BaseWorkforceClaims()
            .Append(new Claim(guestClaimType, guestClaimValue))
            .ToList();
        if (guestClaimType != "acct")
        {
            claims.Add(new Claim("acct", "0"));
        }

        var subject = BuildSubject(claims, groups: [EmpGroupSid]);
        InternalAudienceResolver.IsInternal(subject, DefaultPolicy(), realm: WorkforceRealm()).ShouldBeFalse();
    }

    [Fact]
    public void Missing_tid_not_internal()
    {
        var claims = new Claim[]
        {
            new("iss", $"https://login.microsoftonline.com/{TrustedTenantUpper}/v2.0"),
            new("aud", "api://autheris-gw"),
            new("acct", "0")
        };

        var subject = BuildSubject(claims);
        InternalAudienceResolver.IsInternal(subject, DefaultPolicy(), realm: WorkforceRealm()).ShouldBeFalse();
    }

    [Fact]
    public void Missing_acct_indicator_not_internal_unless_employee_group()
    {
        var strictSubject = BuildSubject(BaseWorkforceClaims());
        InternalAudienceResolver.IsInternal(strictSubject, DefaultPolicy(requireMemberIndicator: true), realm: WorkforceRealm())
            .ShouldBeFalse();

        var employeeSubject = BuildSubject(BaseWorkforceClaims(), groups: [EmpGroupSid]);
        InternalAudienceResolver.IsInternal(employeeSubject, DefaultPolicy(requireMemberIndicator: true), realm: WorkforceRealm())
            .ShouldBeTrue();
    }

    [Fact]
    public void Generic_oidc_realm_tid_ignored()
    {
        var genericRealm = new IdentityRealm(
            id: "keycloak-oidc",
            kind: IdentityRealmKind.GenericOidc,
            tenantBinding: TenantBindingSpecification.Fixed(new TenantId("t1")),
            issuers: [$"https://login.microsoftonline.com/{TrustedTenantUpper}/v2.0"],
            audiences: ["api://autheris-gw"]);

        var claims = BaseWorkforceClaims().Append(new Claim("acct", "0"));
        var subject = BuildSubject(claims, realmKind: IdentityRealmKind.GenericOidc);

        InternalAudienceResolver.IsInternal(subject, DefaultPolicy(), realm: genericRealm).ShouldBeFalse();
    }

    [Fact]
    public void Tid_guid_normalised()
    {
        var bracedUpperTid = "{" + TrustedTenantUpper.ToUpperInvariant() + "}";
        var claims = new Claim[]
        {
            new("tid", bracedUpperTid),
            new("iss", $"https://login.microsoftonline.com/{TrustedTenantUpper.ToLowerInvariant()}/v2.0"),
            new("aud", "api://autheris-gw"),
            new("acct", "0")
        };

        var subject = BuildSubject(claims);
        InternalAudienceResolver.IsInternal(subject, DefaultPolicy(), realm: WorkforceRealm()).ShouldBeTrue();
    }

    [Fact]
    public void ServicePrincipal_with_trusted_tid_and_registered_client_is_internal()
    {
        var claims = BaseWorkforceClaims()
            .Append(new Claim("idtyp", "app"))
            .Append(new Claim("azp", TrustedAppId));

        var subject = BuildSubject(claims);
        InternalAudienceResolver.IsInternal(subject, DefaultPolicy(), realm: WorkforceRealm()).ShouldBeTrue();
    }

    [Fact]
    public void ServicePrincipal_unregistered_client_not_internal()
    {
        var claims = BaseWorkforceClaims()
            .Append(new Claim("idtyp", "app"))
            .Append(new Claim("appid", "unregistered-sp-999"));

        var subject = BuildSubject(claims);
        InternalAudienceResolver.IsInternal(subject, DefaultPolicy(), realm: WorkforceRealm()).ShouldBeFalse();
    }

    [Fact]
    public void ServicePrincipal_with_foreign_tid_is_not_internal()
    {
        var claims = BaseWorkforceClaims(tid: ForeignTenantGuid, issTid: ForeignTenantGuid)
            .Append(new Claim("idtyp", "app"))
            .Append(new Claim("azp", TrustedAppId));

        var subject = BuildSubject(claims);
        InternalAudienceResolver.IsInternal(subject, DefaultPolicy(), realm: WorkforceRealm()).ShouldBeFalse();
    }

    [Fact]
    public void ServicePrincipal_in_employee_group_is_internal()
    {
        var claims = BaseWorkforceClaims(tid: ForeignTenantGuid, issTid: ForeignTenantGuid)
            .Append(new Claim("idtyp", "app"))
            .Append(new Claim("azp", "unregistered-sp-1"));

        var subject = BuildSubject(claims, groups: [EmpGroupSid]);
        InternalAudienceResolver.IsInternal(subject, DefaultPolicy(), realm: WorkforceRealm()).ShouldBeTrue();
    }

    [Fact]
    public void Degraded_groups_not_internal()
    {
        var claims = BaseWorkforceClaims().Append(new Claim("acct", "0"));

        var degraded = BuildSubject(claims, groupStatus: GroupResolutionStatus.Degraded, groups: [EmpGroupSid]);
        InternalAudienceResolver.IsInternal(degraded, DefaultPolicy(), realm: WorkforceRealm()).ShouldBeFalse();

        var expired = BuildSubject(
            claims,
            groupStatus: GroupResolutionStatus.Complete,
            groupsExpireAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            groups: [EmpGroupSid]);
        InternalAudienceResolver.IsInternal(expired, DefaultPolicy(), realm: WorkforceRealm()).ShouldBeFalse();
    }

    [Fact]
    public void Mcp_application_identity_kind_classified_as_service_principal_without_idtyp()
    {
        var untrustedClaims = BaseWorkforceClaims()
            .Append(new Claim(Autheris.Application.Mcp.Services.McpProtocolHandler.IdentityKindClaimType,
                Autheris.Application.Mcp.Services.McpProtocolHandler.ApplicationIdentityKind))
            .Append(new Claim("azp", "untrusted-sp-client"));

        var untrustedSubject = BuildSubject(untrustedClaims);
        InternalAudienceResolver.IsInternal(
                untrustedSubject,
                DefaultPolicy(requireMemberIndicator: false, trustedSpClients: [TrustedAppId]),
                realm: WorkforceRealm(null))
            .ShouldBeFalse();

        var trustedClaims = BaseWorkforceClaims()
            .Append(new Claim(Autheris.Application.Mcp.Services.McpProtocolHandler.IdentityKindClaimType,
                Autheris.Application.Mcp.Services.McpProtocolHandler.ApplicationIdentityKind))
            .Append(new Claim("azp", TrustedAppId));

        var trustedSubject = BuildSubject(trustedClaims);
        InternalAudienceResolver.IsInternal(
                trustedSubject,
                DefaultPolicy(requireMemberIndicator: false, trustedSpClients: [TrustedAppId]),
                realm: WorkforceRealm(TrustedAppId))
            .ShouldBeTrue();
    }
}
