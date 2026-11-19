namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Identity.Validators;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class SubjectRoleOptionsValidatorTests
{
    private static IdentityRealmsOptions Options(Action<IdentityRealmConfigOptions>? tweak = null, params SubjectRoleMapping[] entries)
    {
        var realm = new IdentityRealmConfigOptions
        {
            Kind = IdentityRealmKind.GenericOidc,
            Issuers = ["https://issuer.example"],
            Audiences = ["autheris"],
            UserKeyClaim = "sub",
            FixedTenantId = "tenant-a",
            GroupSource = RealmGroupSource.Resolver,
            ResolverChain = ["subject-roles"],
            AllowedGatewayRoles = ["TenantAdmin", "GovernanceAdmin"],
            SubjectRoles = entries.Length > 0 ? [.. entries] : [Entry("system:serviceaccount:data:dbt", "TenantAdmin")]
        };
        tweak?.Invoke(realm);
        return new IdentityRealmsOptions { Realms = new(StringComparer.OrdinalIgnoreCase) { ["workload"] = realm } };
    }

    private static SubjectRoleMapping Entry(string subject, params string[] roles) => new() { Subject = subject, Roles = [.. roles] };

    private static bool Fails(IdentityRealmsOptions options) => new SubjectRoleOptionsValidator().Validate(null, options).Failed;

    [Fact]
    public void Valid_Configuration_Succeeds() => Fails(Options()).ShouldBeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Startup_EmptySubject_Fails(string subject) => Fails(Options(null, Entry(subject, "TenantAdmin"))).ShouldBeTrue();

    [Theory]
    [InlineData("*")]
    [InlineData("a?b")]
    [InlineData("a%b")]
    [InlineData("a[b")]
    [InlineData("a]b")]
    [InlineData("a^b")]
    [InlineData("a$b")]
    [InlineData("a|b")]
    public void Startup_WildcardCharacters_Fails(string subject) => Fails(Options(null, Entry(subject, "TenantAdmin"))).ShouldBeTrue();

    [Fact]
    public void Startup_PaddedOrControlSubject_Fails()
    {
        Fails(Options(null, Entry(" a", "TenantAdmin"))).ShouldBeTrue();
        Fails(Options(null, Entry("a\nb", "TenantAdmin"))).ShouldBeTrue();
    }

    [Fact]
    public void Startup_DuplicateSubject_Fails() =>
        Fails(Options(null, Entry("a:b", "TenantAdmin"), Entry("a:b", "GovernanceAdmin"))).ShouldBeTrue();

    [Fact]
    public void Startup_UnknownRole_Fails() => Fails(Options(null, Entry("a:b", "Wizard"))).ShouldBeTrue();

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public void Startup_NumericRole_Fails(string role) => Fails(Options(null, Entry("a:b", role))).ShouldBeTrue();

    [Fact]
    public void Startup_RoleNotInAllowedGatewayRoles_Fails() => Fails(Options(null, Entry("a:b", "SecurityAuditor"))).ShouldBeTrue();

    [Fact]
    public void Startup_EmptyRoleList_Fails() => Fails(Options(null, Entry("a:b"))).ShouldBeTrue();

    [Fact]
    public void Startup_LimitsExceeded_Fails()
    {
        var many = Enumerable.Range(0, 33).Select(i => Entry($"s:{i}", "TenantAdmin")).ToArray();
        Fails(Options(null, many)).ShouldBeTrue();
        Fails(Options(null, Entry("a:b", [.. Enumerable.Repeat("TenantAdmin", 9)]))).ShouldBeTrue();
        Fails(Options(null, Entry(new string('x', 257), "TenantAdmin"))).ShouldBeTrue();
    }

    [Fact]
    public void Startup_NonGenericOidcRealm_Fails() =>
        Fails(Options(r => r.Kind = IdentityRealmKind.EntraWorkforce)).ShouldBeTrue();

    [Fact]
    public void Startup_UserKeyClaimNotSub_Fails() => Fails(Options(r => r.UserKeyClaim = "oid")).ShouldBeTrue();

    [Fact]
    public void Startup_ChainWithoutSubjectRolesProvider_Fails() =>
        Fails(Options(r => r.ResolverChain = ["scim-store"])).ShouldBeTrue();

    [Fact]
    public void Startup_NonFixedTenantBinding_Fails()
    {
        Fails(Options(r => r.FixedTenantId = null)).ShouldBeTrue();
        Fails(Options(r =>
        {
            r.FixedTenantId = null;
            r.TenantClaimName = "tid";
            r.TenantClaimMapping = new Dictionary<string, string> { ["x"] = "tenant-b" };
        })).ShouldBeTrue();
    }

    [Fact]
    public void Startup_NonResolverGroupSource_Fails() => Fails(Options(r => r.GroupSource = RealmGroupSource.Token)).ShouldBeTrue();

    [Fact]
    public void Startup_ClusterAdminWithoutOptInFlag_Fails() =>
        Fails(Options(r => r.AllowedGatewayRoles = ["ClusterAdmin"], Entry("a:b", "ClusterAdmin"))).ShouldBeTrue();

    [Fact]
    public void Startup_ClusterAdminWithoutCeilingEntry_Fails() =>
        Fails(Options(r => r.AllowPrivilegedSubjectRoles = true, Entry("a:b", "ClusterAdmin"))).ShouldBeTrue();

    [Fact]
    public void Startup_ClusterAdminWithoutAudiences_Fails() =>
        Fails(Options(r =>
        {
            r.AllowedGatewayRoles = ["ClusterAdmin"];
            r.AllowPrivilegedSubjectRoles = true;
            r.Audiences = [];
        }, Entry("a:b", "ClusterAdmin"))).ShouldBeTrue();

    [Fact]
    public void Startup_ClusterAdminWithFlagAndFixedTenant_Succeeds() =>
        Fails(Options(r =>
        {
            r.AllowedGatewayRoles = ["ClusterAdmin"];
            r.AllowPrivilegedSubjectRoles = true;
        }, Entry("a:b", "ClusterAdmin"))).ShouldBeFalse();

    [Fact]
    public void Startup_PrivilegedFlagWithoutMapping_Fails() =>
        Fails(Options(r =>
        {
            r.SubjectRoles = [];
            r.AllowPrivilegedSubjectRoles = true;
        })).ShouldBeTrue();

    [Fact]
    public void Startup_FailureMessage_DoesNotEchoSubject()
    {
        var result = new SubjectRoleOptionsValidator().Validate(null, Options(null, Entry("secret:subject:value", "Wizard")));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotContain("secret:subject:value");
    }
}
