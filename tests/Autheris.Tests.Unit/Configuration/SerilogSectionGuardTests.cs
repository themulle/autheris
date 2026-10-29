namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using Autheris.Api.Configuration;
using Autheris.Tests.Common;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

public sealed class SerilogSectionGuardTests
{
    [Fact]
    public void NullConfiguration_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => SerilogSectionGuard.ThrowIfConfigured(null!));
    }

    [Fact]
    public void CleanConfig_DoesNotThrowOrWarn()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Gateway:Telemetry:Otlp:Endpoint"] = "http://collector:4317"
            })
            .Build();

        Should.NotThrow(() => SerilogSectionGuard.ThrowIfConfigured(config));
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public void Serilog_Section_Always_Fails_Startup(string? legacySwitch)
    {
        // L1 / DEF-SC-11: the AUTHERIS_ALLOW_LEGACY_CONFIG switch is gone; the removed section fails startup whatever the value of the switch.
        // The message names the section and never a value.
        using var env = new EnvVarScope("AUTHERIS_ALLOW_LEGACY_CONFIG", legacySwitch);
        var values = new Dictionary<string, string?> { ["Serilog:MinimumLevel"] = "SECRET-VALUE-MUST-NOT-LEAK" };
        if (legacySwitch is not null)
        {
            values["AUTHERIS_ALLOW_LEGACY_CONFIG"] = legacySwitch;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var ex = Should.Throw<InvalidOperationException>(() => SerilogSectionGuard.ThrowIfConfigured(config));

        ex.Message.ShouldContain("The 'Serilog' configuration section is no longer supported");
        ex.Message.ShouldNotContain("SECRET-VALUE-MUST-NOT-LEAK");
    }

    [Fact]
    public void OtlpHeaders_WithStringValue_ThrowsSecurityException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Telemetry:Otlp:Headers"] = "Authorization=Bearer sensitive_token"
            })
            .Build();

        var ex = Should.Throw<InvalidOperationException>(() => SerilogSectionGuard.ThrowIfConfigured(config));
        ex.Message.ShouldContain("SEC-OT06-14");
        ex.Message.ShouldContain("OTEL_EXPORTER_OTLP_HEADERS");
    }

    [Fact]
    public void OtlpHeaders_WithChildKeys_ThrowsSecurityException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Telemetry:Otlp:Headers:Authorization"] = "Bearer sensitive_token"
            })
            .Build();

        var ex = Should.Throw<InvalidOperationException>(() => SerilogSectionGuard.ThrowIfConfigured(config));
        ex.Message.ShouldContain("SEC-OT06-14");
    }

    [Fact]
    public void OtlpHeaders_Always_Throw()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Telemetry:Otlp:Headers:X-ApiKey"] = "secret123"
            })
            .Build();

        var ex = Should.Throw<InvalidOperationException>(() => SerilogSectionGuard.ThrowIfConfigured(config));
        ex.Message.ShouldContain("SEC-OT06-14");
    }
}
