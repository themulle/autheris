namespace Autheris.Tests.Unit.Configuration;

using System.Linq;
using Autheris.Api.Telemetry;
using Autheris.Domain.Options.Observability;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>ARCH-MNT-06-T-05 (SC-01, SC-02): plaintext OTLP to a non-loopback host fails the start except in Development or with the flag.</summary>
[Trait("Category", "Security")]
public sealed class OtlpTransportOptionsValidatorTests
{
    private static TelemetryOptions Options(bool allowInsecure = false, bool enabled = true) =>
        new() { Enabled = enabled, Otlp = new OtlpOptions { AllowInsecureTransport = allowInsecure } };

    private static OtlpTransportOptionsValidator Validator(string? traces, string? metrics = null, string? logs = null, string environment = "Production")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        return new OtlpTransportOptionsValidator(env, null, (_, signal) => signal switch
        {
            "TRACES" => traces,
            "METRICS" => metrics,
            _ => logs
        });
    }

    [Theory]
    [InlineData("http://collector.example.com:4317")]
    [InlineData("http://10.0.0.5:4317")]
    [InlineData("http://localhost.evil.com:4317")]
    [InlineData("http://127.0.0.1.evil.com:4317")]
    [InlineData("http://evil.com/localhost")]
    [InlineData("http://[2001:db8::1]:4317")]
    public void ARCH_MNT_06_T_05_Otlp_PlaintextNonLoopback_FailsStart(string endpoint)
    {
        Validator(endpoint).Validate(null, Options()).Failed.ShouldBeTrue();
        Validator(null, endpoint).Validate(null, Options()).Failed.ShouldBeTrue();
        var result = Validator(null, null, endpoint).Validate(null, Options());
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotContain(endpoint, Case.Insensitive, "the endpoint value can carry credentials");
    }

    [Theory]
    [InlineData("http://localhost:4317")]
    [InlineData("http://LOCALHOST:4317")]
    [InlineData("http://127.0.0.1:4317")]
    [InlineData("http://127.5.6.7:4317")]
    [InlineData("http://[::1]:4317")]
    [InlineData("https://collector.example.com:4317")]
    public void ARCH_MNT_06_T_05_Otlp_LoopbackAndHttps_AreAllowed(string endpoint) =>
        Validator(endpoint, endpoint, endpoint).Validate(null, Options()).Succeeded.ShouldBeTrue();

    [Fact]
    public void ARCH_MNT_06_T_05_Otlp_PlaintextNonLoopback_IsAllowedInDevelopment() =>
        Validator("http://collector.example.com:4317", environment: "Development").Validate(null, Options()).Succeeded.ShouldBeTrue();

    [Fact]
    public void ARCH_MNT_06_T_05_Otlp_PlaintextNonLoopback_IsAllowedWithFlag() =>
        Validator("http://collector.example.com:4317").Validate(null, Options(allowInsecure: true)).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("not a uri")]
    [InlineData("collector:4317")]
    [InlineData("ftp://collector.example.com")]
    [InlineData("/relative")]
    public void ARCH_MNT_06_T_05_Otlp_InvalidEndpoint_FailsStart_EvenInDevelopmentOrWithFlag(string endpoint)
    {
        Validator(endpoint).Validate(null, Options(allowInsecure: true)).Failed.ShouldBeTrue();
        Validator(endpoint, environment: "Development").Validate(null, Options()).Failed.ShouldBeTrue();
    }

    [Fact]
    public void ARCH_MNT_06_T_05_Otlp_NoEndpoint_OrTelemetryDisabled_Succeeds()
    {
        Validator(null).Validate(null, Options()).Succeeded.ShouldBeTrue();
        Validator("http://collector.example.com").Validate(null, Options(enabled: false)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ARCH_MNT_06_T_05_Otlp_UnknownEnvironment_FailsClosed()
    {
        var validator = new OtlpTransportOptionsValidator(null, null, (_, _) => "http://collector.example.com");
        validator.Validate(null, Options()).Failed.ShouldBeTrue();
        validator.Validate(null, Options()).Failures!.Count().ShouldBe(3);
    }
}
