namespace Autheris.Tests.Unit.Identity;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Validators;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

/// <summary>DT-A-11 / DT-SC-14: identity realms are explicit; no implicit <c>*-legacy</c> realm exists.</summary>
[Trait("Category", "Security")]
public sealed class ExplicitRealmsTests
{
    public static TheoryData<string, IdentityRealmKind> Schemes => new()
    {
        { "EntraId", IdentityRealmKind.EntraWorkforce },
        { "Adfs", IdentityRealmKind.Adfs },
        { "BasicAuth", IdentityRealmKind.Basic },
        { "ForwardAuth", IdentityRealmKind.ForwardAuth },
        { "Kerberos", IdentityRealmKind.Kerberos },
    };

    private static GatewayOptions Gateway(string scheme, string? defaultTenant = "tenant-d") => new()
    {
        Tenancy = new TenancyOptions { DefaultTenantId = defaultTenant },
        Authentication = scheme switch
        {
            "EntraId" => new AuthenticationOptions { EntraId = new EntraIdAuthOptions { Enabled = true } },
            "Adfs" => new AuthenticationOptions { Adfs = new AdfsAuthOptions { Enabled = true } },
            "BasicAuth" => new AuthenticationOptions { BasicAuth = new BasicAuthOptions { Enabled = true } },
            "ForwardAuth" => new AuthenticationOptions { ForwardAuth = new ForwardAuthOptions { Enabled = true } },
            _ => new AuthenticationOptions { EnableKerberos = true },
        },
    };

    private static IdentityRealmsOptions Realms(IdentityRealmKind kind, string? fixedTenant = null)
    {
        var options = new IdentityRealmsOptions();
        options.Realms["explicit"] = new IdentityRealmConfigOptions { Kind = kind, FixedTenantId = fixedTenant };
        return options;
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public void Enabled_Scheme_Without_Realm_Fails_Startup_DT_SC_14(string scheme, IdentityRealmKind kind)
    {
        var failures = IdentityRealmSchemeValidator.FindFailures(Gateway(scheme), new IdentityRealmsOptions());

        failures.ShouldHaveSingleItem().ShouldContain(scheme);
        failures[0].ShouldContain(kind.ToString());
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public void Enabled_Scheme_With_Realm_Of_Another_Kind_Fails_Startup_DT_SC_14(string scheme, IdentityRealmKind kind)
    {
        var other = kind == IdentityRealmKind.GenericOidc ? IdentityRealmKind.Pat : IdentityRealmKind.GenericOidc;

        IdentityRealmSchemeValidator.FindFailures(Gateway(scheme), Realms(other)).ShouldNotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public void Enabled_Scheme_With_Matching_Realm_Starts_DT_SC_14(string scheme, IdentityRealmKind kind) =>
        IdentityRealmSchemeValidator.FindFailures(Gateway(scheme), Realms(kind)).ShouldBeEmpty();

    [Fact]
    public void Realm_Without_Binding_And_Without_Default_Fails_Startup_DT_SC_14()
    {
        var failures = IdentityRealmSchemeValidator.FindFailures(
            Gateway("BasicAuth", defaultTenant: null), Realms(IdentityRealmKind.Basic));

        failures.ShouldHaveSingleItem().ShouldContain("explicit");
    }

    [Fact]
    public void Realm_With_Fixed_Tenant_Starts_Without_Default_DT_SC_14() =>
        IdentityRealmSchemeValidator.FindFailures(
            Gateway("BasicAuth", defaultTenant: null), Realms(IdentityRealmKind.Basic, fixedTenant: "tenant-a")).ShouldBeEmpty();

    [Fact]
    public void Disabled_Schemes_Need_No_Realm_DT_SC_14() =>
        IdentityRealmSchemeValidator.FindFailures(new GatewayOptions(), new IdentityRealmsOptions()).ShouldBeEmpty();

    [Theory]
    [MemberData(nameof(Schemes))]
    public void No_Implicit_Realm_Is_Registered_DT_SC_14(string scheme, IdentityRealmKind kind)
    {
        _ = kind;
        var registry = new IdentityRealmRegistry();

        registry.Initialize(new IdentityRealmsOptions());

        registry.GetAllRealms().ShouldBeEmpty();
        registry.GetRealm("workforce-entra-legacy").ShouldBeNull();
        registry.GetRealm("legacy-basic").ShouldBeNull();
        scheme.ShouldNotBeNull();
    }

    [Fact]
    public void Explicit_Realm_Grants_Only_Allowed_Roles_DT_SC_14()
    {
        var registry = new IdentityRealmRegistry();
        var options = Realms(IdentityRealmKind.Basic, "tenant-a");
        options.Realms["explicit"].AllowedGatewayRoles = ["Consumer"];

        registry.Initialize(options);

        registry.GetRealm("explicit")!.AllowedGatewayRoles.ToList().ShouldBe([GatewayRole.Consumer]);
    }

    [Fact]
    public void Validator_Messages_Name_The_Scheme_Not_Values()
    {
        var gateway = Gateway("BasicAuth");
        var validator = new IdentityRealmSchemeValidator(gateway);

        var result = validator.Validate(null, new IdentityRealmsOptions());

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("BasicAuth");
        new List<string>(result.Failures!).Count.ShouldBe(1);
    }
}
