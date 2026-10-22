namespace Autheris.Tests.Unit.Identity;

using Autheris.Application.Identity.Validators;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Shouldly;
using Xunit;

public sealed class IdentityRealmsScimOptionsValidatorTests
{
    private readonly ScimOptionsValidator _validator = new();

    [Fact]
    public void ScimOptionsValidator_DuplicateSecretAcrossRealms_Fails()
    {
        const string sharedSecret = "LongEnoughSecret12345678901234567890";
        var options = new IdentityRealmsOptions();
        options.Realms["realm-a"] = new IdentityRealmConfigOptions
        {
            Provisioning = RealmProvisioningMode.Scim,
            Scim = new ScimRealmSecretOptions { PrimarySecret = sharedSecret }
        };
        options.Realms["realm-b"] = new IdentityRealmConfigOptions
        {
            Provisioning = RealmProvisioningMode.Scim,
            Scim = new ScimRealmSecretOptions { PrimarySecret = sharedSecret }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("duplicate");
    }

    [Fact]
    public void ScimOptionsValidator_ScimRealmWithoutSecret_Fails()
    {
        var options = new IdentityRealmsOptions();
        options.Realms["scim-realm"] = new IdentityRealmConfigOptions
        {
            Provisioning = RealmProvisioningMode.Scim,
            Scim = new ScimRealmSecretOptions { PrimarySecret = null, SecondarySecret = "" }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("must have at least one secret configured");
    }

    [Fact]
    public void ScimOptionsValidator_ShortSecret_Fails()
    {
        var options = new IdentityRealmsOptions();
        options.Realms["scim-realm"] = new IdentityRealmConfigOptions
        {
            Provisioning = RealmProvisioningMode.Scim,
            Scim = new ScimRealmSecretOptions { PrimarySecret = "TooShortSecret123" }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("shorter than");
    }

    [Fact]
    public void ScimOptionsValidator_ValidSecrets_Succeeds()
    {
        var options = new IdentityRealmsOptions();
        options.Realms["realm-a"] = new IdentityRealmConfigOptions
        {
            Provisioning = RealmProvisioningMode.Scim,
            Scim = new ScimRealmSecretOptions
            {
                PrimarySecret = "SecretForRealmA_12345678901234567890",
                SecondarySecret = "SecondaryRealmA_12345678901234567890"
            }
        };
        options.Realms["realm-b"] = new IdentityRealmConfigOptions
        {
            Provisioning = RealmProvisioningMode.Scim,
            Scim = new ScimRealmSecretOptions
            {
                PrimarySecret = "SecretForRealmB_12345678901234567890"
            }
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ScimOptionsValidator_NonScimRealm_Ignored()
    {
        var options = new IdentityRealmsOptions();
        options.Realms["jit-realm"] = new IdentityRealmConfigOptions
        {
            Provisioning = RealmProvisioningMode.Jit,
            Scim = null
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }
}
