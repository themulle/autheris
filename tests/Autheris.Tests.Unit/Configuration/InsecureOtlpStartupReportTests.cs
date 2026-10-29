namespace Autheris.Tests.Unit.Configuration;

using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Extensions.DependencyInjection.StartupChecks;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Observability;
using Autheris.Tests.Unit.Common;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Configuration.StartupValidatorGoldenSupport;

/// <summary>ARCH-MNT-06-FU-03: insecure OTLP transport is a WARN entry of the startup report (WAVE3-STARTUP-SC-01, SC-02).</summary>
[Collection(GoldenConsoleCollection.Name)]
public sealed class InsecureOtlpStartupReportTests : IDisposable
{
    private const string Key = "otlp_allow_insecure_transport (Gateway:Telemetry:Otlp:AllowInsecureTransport)";
    private readonly string _fixtureDir = CreateFixtureDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_fixtureDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup of the temp fixture
        }
    }

    private static TelemetryOptions Telemetry(bool enabled = true, bool insecure = true) =>
        new() { Enabled = enabled, Otlp = new OtlpOptions { AllowInsecureTransport = insecure, Endpoint = "http://user:s3cret@collector.internal:4317/path?token=abc", } };

    private string Run(GoldenCase golden, TelemetryOptions? telemetry)
    {
        ValidateAction validate = (o, e, g, l) => GatewayStartupValidator.ValidateStartup(o, e, g, l, GatewayStartupChecks.All, telemetry);
        return RunWarningOutcome(golden, validate, _fixtureDir);
    }

    [Fact]
    public void InsecureOtlp_FlagSet_ReportedAsWarning()
    {
        var output = Run(Dev("d"), Telemetry());
        output.ShouldContain("WARN:" + Key);
        output.ShouldContain("outcome: OK");
    }

    [Fact]
    public void InsecureOtlp_FlagUnset_NotReported() => Run(Dev("d"), Telemetry(insecure: false)).ShouldNotContain("otlp_allow_insecure_transport");

    [Fact]
    public void InsecureOtlp_TelemetryDisabled_NotReported() => Run(Dev("d"), Telemetry(enabled: false)).ShouldNotContain("otlp_allow_insecure_transport");

    [Fact]
    public void InsecureOtlp_NoTelemetryOptions_NotReported() => Run(Dev("d"), null).ShouldNotContain("otlp_allow_insecure_transport");

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "WAVE3-STARTUP-SC-01")]
    public void InsecureOtlp_Production_WithoutWarnFlag_FailsStartup()
    {
        var output = Run(Prod("p"), Telemetry());
        output.ShouldContain(nameof(ValidationException));
        output.ShouldContain("AllowInsecureWarnFlagsInProduction");
        output.ShouldContain("Gateway:Telemetry:Otlp:AllowInsecureTransport");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "WAVE3-STARTUP-SC-01")]
    public void InsecureOtlp_Production_WithWarnFlag_OnlyWarning()
    {
        var output = Run(Prod("p", ("AllowInsecureWarnFlagsInProduction", "true")), Telemetry());
        output.ShouldContain("outcome: OK");
        output.ShouldContain("WARN:" + Key);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "WAVE3-STARTUP-SC-01")]
    public void InsecureOtlp_Production_FlagUnset_StillPasses() => Run(Prod("p"), Telemetry(insecure: false)).ShouldContain("outcome: OK");

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "WAVE3-STARTUP-SC-02")]
    public void InsecureOtlpWarning_DoesNotContainEndpointOrHeaders()
    {
        foreach (var golden in new[] { Dev("d"), Prod("p", ("AllowInsecureWarnFlagsInProduction", "true")), Prod("f") })
        {
            var output = Run(golden, Telemetry());
            output.ShouldNotContain("collector.internal");
            output.ShouldNotContain("s3cret");
            output.ShouldNotContain("token=abc");
        }
    }

    [Fact]
    public void InsecureOtlp_DoesNotChangeOptionsWarningList()
    {
        // The health component reads GatewayOptions.GetActiveWarnings(); the OTLP entry must not leak into it (health unchanged).
        var options = Bind(Dev("d").Config, _fixtureDir);
        var context = new StartupCheckContext(options, HostEnvironment("Development"), _ => null, null, Telemetry());

        context.ExtraWarnings.ShouldBe(["WARN:" + Key]);
        context.Warnings.ShouldBe(options.GetActiveWarnings());
    }
}
