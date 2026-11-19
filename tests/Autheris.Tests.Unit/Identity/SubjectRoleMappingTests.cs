namespace Autheris.Tests.Unit.Identity;

using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class SubjectRoleMappingTests
{
    internal const string Issuer = "https://issuer.example";
    internal const string Subject = "system:serviceaccount:data:dbt-sync";

    internal static IdentityRealm Realm(
        string subject = Subject,
        GatewayRole[]? roles = null,
        GatewayRole[]? allowed = null,
        bool allowPrivileged = true,
        string userKeyClaim = "sub",
        IdentityRealmKind kind = IdentityRealmKind.GenericOidc,
        TenantBindingSpecification? tenant = null) =>
        new(
            "workload",
            kind,
            tenant ?? TenantBindingSpecification.Fixed(new TenantId("tenant-a")),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.None,
            issuers: [Issuer],
            audiences: ["autheris"],
            userKeyClaim: userKeyClaim,
            resolverChain: ["subject-roles"],
            allowedGatewayRoles: new HashSet<GatewayRole>(allowed ?? [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin]))
        {
            SubjectRoles = new Dictionary<string, IReadOnlyList<GatewayRole>>(System.StringComparer.Ordinal)
            {
                [subject] = roles ?? [GatewayRole.ClusterAdmin]
            },
            AllowPrivilegedSubjectRoles = allowPrivileged
        };

    internal static ClaimsIdentity Token(string sub = Subject, string iss = Issuer, params Claim[] extra) =>
        new([new Claim("sub", sub), new Claim("iss", iss), .. extra], "Bearer");

    [Fact]
    public void SubjectRole_ExactSubject_AppliesRole()
    {
        SubjectRoleMapper.Resolve(Realm(), Token()).ShouldBe([GatewayRole.ClusterAdmin]);
    }

    [Fact]
    public void SubjectRole_OtherSubjectSameRealm_GetsNothing()
    {
        SubjectRoleMapper.Resolve(Realm(), Token("system:serviceaccount:data:other")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("SYSTEM:SERVICEACCOUNT:DATA:DBT-SYNC")]
    [InlineData(" system:serviceaccount:data:dbt-sync")]
    [InlineData("system:serviceaccount:data:dbt-sync ")]
    public void SubjectRole_CaseDifferentSubject_GetsNothing(string sub)
    {
        SubjectRoleMapper.Resolve(Realm(), Token(sub)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("system:serviceaccount:data")]
    [InlineData("system:serviceaccount:data:dbt-sync-extra")]
    [InlineData("system:serviceaccount:data:*")]
    [InlineData("*")]
    public void SubjectRole_PrefixOrWildcardSubject_NeverMatches(string sub)
    {
        SubjectRoleMapper.Resolve(Realm(), Token(sub)).ShouldBeEmpty();
        const string pattern = "system:serviceaccount:data:*";
        if (sub != pattern)
        {
            // A configured wildcard is rejected at startup; were it present, it still only equals itself.
            SubjectRoleMapper.Resolve(Realm(pattern), Token(sub)).ShouldBeEmpty();
        }
    }

    [Fact]
    public void SubjectRole_TwoSubClaims_Denied()
    {
        var identity = Token(extra: new Claim("sub", Subject));

        SubjectRoleMapper.Resolve(Realm(), identity).ShouldBeEmpty();
    }

    [Fact]
    public void SubjectRole_IssuerNotOfRealm_NoMapping()
    {
        SubjectRoleMapper.Resolve(Realm(), Token(iss: "https://evil.example")).ShouldBeEmpty();
        SubjectRoleMapper.Resolve(Realm(), Token(iss: Issuer + "/")).ShouldBeEmpty();
        SubjectRoleMapper.Resolve(Realm(), new ClaimsIdentity([new Claim("sub", Subject)], "Bearer")).ShouldBeEmpty();
    }

    [Fact]
    public void SubjectRole_OidClaimPresent_DoesNotChangeMatch()
    {
        SubjectRoleMapper.Resolve(Realm(), Token(extra: new Claim("oid", "someone-else"))).ShouldBe([GatewayRole.ClusterAdmin]);
        SubjectRoleMapper.Resolve(Realm(), Token("other", extra: new Claim("oid", Subject))).ShouldBeEmpty();
    }

    [Fact]
    public void SubjectRole_RealmNotEligible_NoMapping()
    {
        SubjectRoleMapper.Resolve(Realm(userKeyClaim: "oid"), Token()).ShouldBeEmpty();
        SubjectRoleMapper.Resolve(Realm(kind: IdentityRealmKind.EntraWorkforce), Token()).ShouldBeEmpty();
        SubjectRoleMapper.Resolve(Realm(tenant: TenantBindingSpecification.Unbound()), Token()).ShouldBeEmpty();
    }

    [Fact]
    public void SubjectRole_RoleOutsideCeilingOrWithoutOptIn_IsNeverApplied()
    {
        SubjectRoleMapper.Resolve(Realm(roles: [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin], allowed: [GatewayRole.TenantAdmin]), Token())
            .ShouldBe([GatewayRole.TenantAdmin]);
        SubjectRoleMapper.Resolve(Realm(allowPrivileged: false), Token()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ClusterAdmin", true)]
    [InlineData("clusteradmin", true)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData("NoSuchRole", false)]
    public void RoleName_Parse_IsStrict(string name, bool expected)
    {
        SubjectRoleMapper.TryParseRoleName(name, out _).ShouldBe(expected);
    }
}
