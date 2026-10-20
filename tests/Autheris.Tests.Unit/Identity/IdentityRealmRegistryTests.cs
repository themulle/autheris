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
}
