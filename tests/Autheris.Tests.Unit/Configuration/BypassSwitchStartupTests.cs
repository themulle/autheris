namespace Autheris.Tests.Unit.Configuration;

using System;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Extensions.DependencyInjection.StartupChecks;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Configuration.StartupValidatorGoldenSupport;

[Collection(GoldenConsoleCollection.Name)]
[Trait("Category", "Security")]
public sealed class BypassSwitchStartupTests
{
    private static GatewayOptions BuildBypassOptions() =>
        new()
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "kv://masking-hmac" },
            Audit = new AuditOptions { ChainAnchorPath = Path.Combine(Path.GetTempPath(), $"anchor-{Guid.NewGuid():N}") }
        };

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Bypass_refused_outside_dev_test(string environmentName)
    {
        var options = BuildBypassOptions();
        var env = HostEnvironment(environmentName);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayStartupValidator.ValidateStartup(options, env, _ => null, logger: null));

        ex.Message.ShouldContain("danger_bypass_consent_checks");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public void Bypass_allowed_only_in_development_and_test(string environmentName)
    {
        var options = BuildBypassOptions();
        var env = HostEnvironment(environmentName);

        var ex = Record.Exception(() =>
            GatewayStartupValidator.ValidateStartup(
                options,
                env,
                _ => null,
                logger: null,
                [new StartupCheck("ProductionGovernance", ProductionStartupChecks.ProductionGovernance)]));

        ex.ShouldBeNull();
    }
}
