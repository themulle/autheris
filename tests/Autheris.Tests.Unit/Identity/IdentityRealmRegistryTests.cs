namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Validators;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

public sealed class IdentityRealmRegistryTests
{
    [Fact]
    public void IdentityRealmRegistry_ResolvesByIssuer_CaseInsensitiveNormalized()
    {
        var registry = new IdentityRealmRegistry();
        var realm = new IdentityRealm(
            "workforce-entra",
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, "tenant-corp"),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            issuers: ["https://login.microsoftonline.com/tenant-123/v2.0/"]);

        registry.RegisterRealm(realm);

        // Resolves with trailing slash variation and uppercase letters
        var resolved = registry.ResolveByIssuer("HTTPS://LOGIN.MICROSOFTONLINE.COM/TENANT-123/V2.0");
        resolved.ShouldNotBeNull();
        resolved.RealmId.ShouldBe("workforce-entra");
    }

    [Fact]
    public void IdentityRealmRegistry_UnknownIssuer_ThrowsOrReturnsNull()
    {
        var registry = new IdentityRealmRegistry();
        var resolved = registry.ResolveByIssuer("https://malicious-idp.attacker.com");

        resolved.ShouldBeNull();
    }

    [Fact]
    public void IdentityRealmRegistry_SynthesizesLegacyRealms_WhenNotConfigured()
    {
        var registry = new IdentityRealmRegistry();
        var emptyOptions = new IdentityRealmsOptions();
        var legacyOptions = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions
                {
                    Enabled = true,
                    TenantId = "legacy-tenant",
                    Instance = "https://login.microsoftonline.com"
                }
            }
        };

        registry.Initialize(legacyOptions, emptyOptions);

        var defaultRealm = registry.GetDefaultRealm();
        defaultRealm.ShouldNotBeNull();
        defaultRealm.RealmId.ShouldBe("workforce-entra-legacy");

        var resolved = registry.ResolveByIssuer("https://login.microsoftonline.com/legacy-tenant");
        resolved.ShouldNotBeNull();
        resolved.RealmId.ShouldBe("workforce-entra-legacy");
    }

    [Fact]
    public void IdentityRealmOptionsValidator_CustomerRealmBoundToCorporateTenant_FailsValidation()
    {
        var options = new IdentityRealmsOptions();
        options.Realms["customer-realm"] = new IdentityRealmConfigOptions
        {
            Kind = IdentityRealmKind.EntraExternalId,
            FixedTenantId = TenantId.LegacySingleTenant.Value
        };

        var act = () => IdentityRealmOptionsValidator.Validate(options, TenantId.LegacySingleTenant);

        act.ShouldThrow<ValidationException>();
    }

    [Fact]
    public void IdentityRealmRegistry_SidRealmLookup_WiredOnConstruction()
    {
        var registry = new IdentityRealmRegistry();
        var realm = new IdentityRealm(
            "sid-lookup-realm",
            IdentityRealmKind.GenericOidc,
            new TenantBindingSpecification(TenantBindingKind.Fixed, "t1"),
            RealmGroupSource.None,
            RealmProvisioningMode.None);

        registry.RegisterRealm(realm);

        Sid.RealmLookup.ShouldNotBeNull();
        var resolved = Sid.RealmLookup("sid-lookup-realm");
        resolved.ShouldNotBeNull();
        resolved.Id.ShouldBe("sid-lookup-realm");
    }

    [Fact]
    public void IdentityRealmRegistry_DirectIssuerLookup_HitsFastPathCache()
    {
        var registry = new IdentityRealmRegistry();
        var realm = new IdentityRealm(
            "direct-issuer-realm",
            IdentityRealmKind.GenericOidc,
            new TenantBindingSpecification(TenantBindingKind.Fixed, "t1"),
            RealmGroupSource.None,
            RealmProvisioningMode.None,
            issuers: ["https://fastpath.issuer.org"]);

        registry.RegisterRealm(realm);

        var resolvedExact = registry.ResolveByIssuer("https://fastpath.issuer.org");
        resolvedExact.ShouldNotBeNull();
        resolvedExact.Id.ShouldBe("direct-issuer-realm");

        var resolvedTrailingSlash = registry.ResolveByIssuer("https://fastpath.issuer.org/");
        resolvedTrailingSlash.ShouldNotBeNull();
        resolvedTrailingSlash.Id.ShouldBe("direct-issuer-realm");
    }

    [Fact]
    public void IdentityRealmRegistry_ExplicitRealmsInOptions_Populated()
    {
        var registry = new IdentityRealmRegistry();
        var realmsOptions = new IdentityRealmsOptions();
        realmsOptions.Realms["custom-realm-42"] = new IdentityRealmConfigOptions
        {
            Kind = IdentityRealmKind.GenericOidc,
            FixedTenantId = "tenant-xyz",
            Issuers = ["https://custom.issuer.com"]
        };

        registry.Initialize(new GatewayOptions(), realmsOptions);

        var realm = registry.GetRealm("custom-realm-42");
        realm.ShouldNotBeNull();
        realm.Id.ShouldBe("custom-realm-42");
        realm.Kind.ShouldBe(IdentityRealmKind.GenericOidc);
    }

    [Fact]
    public void IdentityRealmRegistry_ExistingWorkforceEntra_SuppressesLegacySynthesis()
    {
        var registry = new IdentityRealmRegistry();
        var realmsOptions = new IdentityRealmsOptions();
        realmsOptions.Realms["workforce-entra"] = new IdentityRealmConfigOptions
        {
            Kind = IdentityRealmKind.EntraWorkforce,
            FixedTenantId = "tenant-primary",
            Issuers = ["https://explicit-entra.microsoft.com"]
        };

        var gatewayOptions = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions
                {
                    Enabled = true,
                    TenantId = "legacy-tenant",
                    Instance = "https://legacy-authority.example.com"
                }
            }
        };

        registry.Initialize(gatewayOptions, realmsOptions);

        var explicitRealm = registry.GetRealm("workforce-entra");
        explicitRealm.ShouldNotBeNull();
        explicitRealm.IsImplicitLegacy.ShouldBeFalse();

        // Must NOT synthesize workforce-entra-legacy when workforce-entra exists
        registry.GetAllRealms().ShouldNotContain(r => r.Id == "workforce-entra-legacy");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IdentityRealmRegistry_ResolveByIssuer_NullOrEmpty_ReturnsNull(string? issuer)
    {
        var registry = new IdentityRealmRegistry();
        registry.ResolveByIssuer(issuer!).ShouldBeNull();
    }

    [Fact]
    public void IdentityRealmRegistry_ResolveByIssuer_PrefixMatching_Succeeds()
    {
        var registry = new IdentityRealmRegistry();
        var realm = new IdentityRealm(
            "corp-realm",
            IdentityRealmKind.GenericOidc,
            new TenantBindingSpecification(TenantBindingKind.Fixed, "t1"),
            RealmGroupSource.None,
            RealmProvisioningMode.None,
            issuers: ["https://sts.corp.com/auth"]);

        registry.RegisterRealm(realm);

        // Subpath / prefix resolution
        var resolved = registry.ResolveByIssuer("https://sts.corp.com/auth/v2/subtenant");
        resolved.ShouldNotBeNull();
        resolved.Id.ShouldBe("corp-realm");
    }

    [Fact]
    public void IdentityRealmRegistry_GetRealm_UnknownOrNullId_ReturnsNull()
    {
        var registry = new IdentityRealmRegistry();
        registry.GetRealm("non-existent-realm").ShouldBeNull();
        registry.GetRealm(null!).ShouldBeNull();
        registry.GetRealm("   ").ShouldBeNull();
    }

    [Fact]
    public void IdentityRealmRegistry_Initialize_WithClaimMappedTenantAndAdfsBasic_SynthesizesCorrectly()
    {
        var registry = new IdentityRealmRegistry();
        var realmsOptions = new IdentityRealmsOptions();
        realmsOptions.Realms["claim-mapped-realm"] = new IdentityRealmConfigOptions
        {
            Kind = IdentityRealmKind.GenericOidc,
            TenantClaimName = "tid",
            TenantClaimMapping = new Dictionary<string, string> { ["aad-guid-1"] = "t1" },
            AllowedGatewayRoles = ["Consumer", "InvalidRoleName"]
        };

        var gatewayOptions = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                Adfs = new AdfsAuthOptions
                {
                    Enabled = true,
                    Authority = "https://adfs.company.com/adfs",
                    Audience = "urn:autheris:adfs"
                },
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true
                }
            }
        };

        registry.Initialize(gatewayOptions, realmsOptions);

        var claimRealm = registry.GetRealm("claim-mapped-realm");
        claimRealm.ShouldNotBeNull();
        claimRealm.TenantBinding.Kind.ShouldBe(TenantBindingKind.ClaimMapped);
        claimRealm.AllowedGatewayRoles.ShouldContain(GatewayRole.Consumer);

        var adfsRealm = registry.GetRealm("corp-adfs-legacy");
        adfsRealm.ShouldNotBeNull();
        adfsRealm.Kind.ShouldBe(IdentityRealmKind.Adfs);
        adfsRealm.Issuers.ShouldContain("https://adfs.company.com/adfs");

        var basicRealm = registry.GetRealm("legacy-basic");
        basicRealm.ShouldNotBeNull();
        basicRealm.Kind.ShouldBe(IdentityRealmKind.Basic);

        registry.GetAllRealms().Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void IdentityRealmRegistry_Initialize_DisabledLegacyOptions_DoNotSynthesizeRealms()
    {
        var registry = new IdentityRealmRegistry();
        var gatewayOptions = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions { Enabled = false },
                Adfs = new AdfsAuthOptions { Enabled = false },
                BasicAuth = new BasicAuthOptions { Enabled = false },
                EnableKerberos = false
            }
        };

        registry.Initialize(gatewayOptions);

        registry.GetRealm("workforce-entra-legacy").ShouldBeNull();
        registry.GetRealm("corp-adfs-legacy").ShouldBeNull();
        registry.GetRealm("legacy-basic").ShouldBeNull();
        registry.GetRealm("legacy-kerberos").ShouldBeNull();
    }

    [Fact]
    public void IdentityRealmRegistry_Initialize_EntraLegacy_PopulatesTenantIssuers()
    {
        var registry = new IdentityRealmRegistry();
        var gatewayOptions = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions
                {
                    Enabled = true,
                    TenantId = "tenant-guid",
                    Audience = "api://autheris"
                }
            }
        };

        registry.Initialize(gatewayOptions);

        var entraRealm = registry.GetRealm("workforce-entra-legacy");
        entraRealm.ShouldNotBeNull();
        entraRealm.Issuers.ShouldContain("https://login.microsoftonline.com/tenant-guid/v2.0");
        entraRealm.Issuers.ShouldContain("https://sts.windows.net/tenant-guid/");
    }

    [Fact]
    public void IdentityRealmRegistry_TryGetRealm_WorksAsExpected()
    {
        var registry = new IdentityRealmRegistry();
        var realm = new IdentityRealm("test-realm", IdentityRealmKind.GenericOidc, new TenantBindingSpecification(TenantBindingKind.Fixed, "t1"), RealmGroupSource.None, RealmProvisioningMode.None, issuers: ["https://example.com"]);
        registry.RegisterRealm(realm);

        registry.TryGetRealm("test-realm", out var found).ShouldBeTrue();
        found.ShouldNotBeNull();
        found.Id.ShouldBe("test-realm");

        registry.TryGetRealm("unknown-realm", out var notFound).ShouldBeFalse();
        notFound.ShouldBeNull();
    }

    [Fact]
    public void IdentityRealmRegistry_GetDefaultRealm_Hierarchy()
    {
        var registry = new IdentityRealmRegistry();
        registry.GetDefaultRealm().ShouldBeNull();

        var customRealm = new IdentityRealm("custom", IdentityRealmKind.GenericOidc, new TenantBindingSpecification(TenantBindingKind.Fixed, "t1"), RealmGroupSource.None, RealmProvisioningMode.None, issuers: ["https://example.com"]);
        registry.RegisterRealm(customRealm);
        registry.GetDefaultRealm()?.Id.ShouldBe("custom");

        var entraRealm = new IdentityRealm("workforce-entra", IdentityRealmKind.EntraWorkforce, new TenantBindingSpecification(TenantBindingKind.Fixed, "t1"), RealmGroupSource.None, RealmProvisioningMode.None, issuers: ["https://login.microsoftonline.com"]);
        registry.RegisterRealm(entraRealm);
        registry.GetDefaultRealm()?.Id.ShouldBe("workforce-entra");

        var defaultRealm = new IdentityRealm("default", IdentityRealmKind.GenericOidc, new TenantBindingSpecification(TenantBindingKind.Fixed, "t1"), RealmGroupSource.None, RealmProvisioningMode.None, issuers: ["https://default.com"]);
        registry.RegisterRealm(defaultRealm);
        registry.GetDefaultRealm()?.Id.ShouldBe("default");
    }

    [Fact]
    public void IdentityRealmRegistry_RegisterRealm_IgnoresWhitespaceIssuers()
    {
        var registry = new IdentityRealmRegistry();
        var realm = new IdentityRealm("ws-realm", IdentityRealmKind.GenericOidc, new TenantBindingSpecification(TenantBindingKind.Fixed, "t1"), RealmGroupSource.None, RealmProvisioningMode.None, issuers: ["", "   ", "https://valid.com"]);
        registry.RegisterRealm(realm);

        registry.ResolveByIssuer("https://valid.com").ShouldNotBeNull();
        registry.ResolveByIssuer("").ShouldBeNull();
    }

    [Fact]
    public void IdentityRealmRegistry_Initialize_WithPartialClaimMapping_FallsBackToFixedLegacy()
    {
        var registry = new IdentityRealmRegistry();
        var realmsOptions = new IdentityRealmsOptions();
        realmsOptions.Realms["partial-claim"] = new IdentityRealmConfigOptions
        {
            Kind = IdentityRealmKind.GenericOidc,
            TenantClaimName = "tid",
            TenantClaimMapping = null
        };

        var gatewayOptions = new GatewayOptions();
        registry.Initialize(gatewayOptions, realmsOptions);

        var realm = registry.GetRealm("partial-claim");
        realm.ShouldNotBeNull();
        realm.TenantBinding.Kind.ShouldBe(TenantBindingKind.Fixed);
    }

    [Fact]
    public void IdentityRealmRegistry_Initialize_WithForwardAuthAndKerberos_SynthesizesCorrectly()
    {
        var registry = new IdentityRealmRegistry();
        var gatewayOptions = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions { Enabled = true },
                EnableKerberos = true
            }
        };

        registry.Initialize(gatewayOptions);

        var forwardAuth = registry.GetRealm("legacy-forwardauth");
        forwardAuth.ShouldNotBeNull();
        forwardAuth.Kind.ShouldBe(IdentityRealmKind.ForwardAuth);

        var kerberos = registry.GetRealm("legacy-kerberos");
        kerberos.ShouldNotBeNull();
        kerberos.Kind.ShouldBe(IdentityRealmKind.Kerberos);
    }
}
