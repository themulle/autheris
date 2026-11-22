namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Providers;
using Autheris.Application.Identity.Validators;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class IdentityRealmsLegacyRoleValidationTests
{
    private static readonly IdentityRealmOptionsValidator Validator = new();

    [Theory]
    [InlineData("DataOwner", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("DataSteward", IdentityRealmKind.EntraWorkforce, RealmGroupSource.Resolver)]
    [InlineData("Auditor", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("Analyst", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("Operator", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("SchemaAdmin", IdentityRealmKind.EntraWorkforce, RealmGroupSource.Resolver)]
    [InlineData("SchemaAdmin", IdentityRealmKind.GenericOidc, RealmGroupSource.Token)]
    [InlineData("SecurityAdmin", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("GatewayAdmin", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("PlatformAdmin", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("PrivacyAdmin", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("DataProtectionOfficer", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("AiAgent", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("McpCaller", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("Developer", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("Approver", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("UnknownRole", IdentityRealmKind.GenericOidc, RealmGroupSource.Token)]
    [InlineData("1", IdentityRealmKind.GenericOidc, RealmGroupSource.None)]
    [InlineData("1", IdentityRealmKind.GenericOidc, RealmGroupSource.Token)]
    public void Removed_role_name_fails_startup(
        string invalidRole,
        IdentityRealmKind kind,
        RealmGroupSource groupSource)
    {
        var options = new IdentityRealmsOptions
        {
            Realms =
            {
                ["realm-1"] = new IdentityRealmConfigOptions
                {
                    Kind = kind,
                    FixedTenantId = "tenant-a",
                    GroupSource = groupSource,
                    Issuers = ["https://idp.example.com/"],
                    AllowedGatewayRoles = [invalidRole],
                },
            },
        };

        var result = Validator.Validate(null, options);
        result.Failed.ShouldBeTrue($"AllowedGatewayRoles containing '{invalidRole}' must fail startup validation.");
    }

    [Theory]
    [InlineData("DataOwner")]
    [InlineData("DataSteward")]
    [InlineData("Auditor")]
    [InlineData("Analyst")]
    [InlineData("SecurityAdmin")]
    [InlineData("UnknownRole")]
    [InlineData("1")]
    public void StaticMappingRoleProvider_rejects_removed_or_unknown_gateway_role(string invalidRole)
    {
        var provider = new StaticMappingRoleProvider();

        Should.Throw<ArgumentException>(() => provider.AddMapping("user-1", roles: [invalidRole]));
    }

    [Fact]
    public void Valid_canonical_gateway_roles_pass_validator_and_static_mapping()
    {
        var options = new IdentityRealmsOptions
        {
            Realms =
            {
                ["realm-1"] = new IdentityRealmConfigOptions
                {
                    Kind = IdentityRealmKind.GenericOidc,
                    FixedTenantId = "tenant-a",
                    GroupSource = RealmGroupSource.None,
                    Issuers = ["https://idp.example.com/"],
                    AllowedGatewayRoles = ["Consumer", "SecurityAuditor", "GovernanceAdmin"],
                },
            },
        };

        Validator.Validate(null, options).Succeeded.ShouldBeTrue();

        var provider = new StaticMappingRoleProvider();
        provider.AddMapping("user-1", roles: ["Consumer", "tenant-a:GovernanceAdmin"]);
    }

    [Theory]
    [InlineData("DataOwner")]
    [InlineData("DataSteward")]
    [InlineData("Auditor")]
    [InlineData("SchemaAdmin")]
    [InlineData("UnknownRole")]
    [InlineData("1")]
    public void SubjectRoles_with_removed_or_numeric_role_fails_startup(string invalidRole)
    {
        var options = new IdentityRealmsOptions
        {
            Realms =
            {
                ["realm-subject"] = new IdentityRealmConfigOptions
                {
                    Kind = IdentityRealmKind.GenericOidc,
                    FixedTenantId = "tenant-a",
                    GroupSource = RealmGroupSource.None,
                    Issuers = ["https://idp.example.com/"],
                    AllowedGatewayRoles = ["Consumer"],
                    SubjectRoles =
                    [
                        new SubjectRoleMapping
                        {
                            Subject = "svc-workload-1",
                            Roles = [invalidRole],
                        },
                    ],
                },
            },
        };

        var result = Validator.Validate(null, options);
        result.Failed.ShouldBeTrue($"SubjectRoles entry containing '{invalidRole}' must fail startup validation.");
    }

    [Fact]
    public void IdentityRealmRegistry_Initialize_Ignores_Numeric_And_Legacy_AllowedGatewayRoles()
    {
        var registry = new IdentityRealmRegistry();
        var options = new IdentityRealmsOptions
        {
            Realms =
            {
                ["realm-token"] = new IdentityRealmConfigOptions
                {
                    Kind = IdentityRealmKind.GenericOidc,
                    FixedTenantId = "tenant-a",
                    GroupSource = RealmGroupSource.Token,
                    Issuers = ["https://idp.example.com/"],
                    AllowedGatewayRoles = ["1", "DataOwner", "SecurityAuditor"],
                },
            },
        };

        registry.Initialize(options);
        var realm = registry.GetRealm("realm-token");
        realm.ShouldNotBeNull();
        realm.AllowedGatewayRoles.ShouldBe(new HashSet<GatewayRole> { GatewayRole.SecurityAuditor });
    }
}
