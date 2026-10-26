namespace Autheris.Tests.Unit.Configuration;

using System;
using Autheris.Tests.Common;
using System.Collections.Generic;
using System.IO;
using Autheris.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

public sealed class SerilogSectionGuardTests
{
    [Fact]
    public void NullConfiguration_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => SerilogSectionGuard.CheckConfiguration(null!));
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

        using var writer = new StringWriter();
        SerilogSectionGuard.CheckConfiguration(config, writer);

        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void SerilogSection_WithDefaultGracefulTolerance_EmitsWarning_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Serilog:WriteTo:0:Name"] = "Console"
            })
            .Build();

        using var writer = new StringWriter();
        SerilogSectionGuard.CheckConfiguration(config, writer);

        var output = writer.ToString();
        output.ShouldContain("WARNING: The 'Serilog' configuration section is deprecated and ignored in Autheris v1.2.5+");
        output.ShouldContain("Logging:LogLevel");
        output.ShouldContain("Gateway:Telemetry:Otlp");
    }

    [Fact]
    public void SerilogSection_WithAllowLegacyTrue_EmitsWarning_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AUTHERIS_ALLOW_LEGACY_CONFIG"] = "true",
                ["Serilog:MinimumLevel"] = "Debug"
            })
            .Build();

        using var writer = new StringWriter();
        SerilogSectionGuard.CheckConfiguration(config, writer);

        writer.ToString().ShouldContain(SerilogSectionGuard.DeprecationWarningMessage);
    }

    [Fact]
    public void SerilogSection_WithAllowLegacyFalseInConfig_ThrowsInvalidOperationException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AUTHERIS_ALLOW_LEGACY_CONFIG"] = "false",
                ["Serilog:MinimumLevel"] = "Debug"
            })
            .Build();

        using var writer = new StringWriter();
        var ex = Should.Throw<InvalidOperationException>(() => SerilogSectionGuard.CheckConfiguration(config, writer));
        ex.Message.ShouldContain("The 'Serilog' configuration section is no longer supported");
        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void SerilogSection_WithEnvVarAllowLegacyFalse_ThrowsInvalidOperationException()
    {
        using var env = new EnvVarScope("AUTHERIS_ALLOW_LEGACY_CONFIG", "false");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel"] = "Debug"
            })
            .Build();

        using var writer = new StringWriter();
        var ex = Should.Throw<InvalidOperationException>(() => SerilogSectionGuard.CheckConfiguration(config, writer));
        ex.Message.ShouldContain("The 'Serilog' configuration section is no longer supported");
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
    public void OtlpHeaders_ThrowsEvenWhenLegacyConfigAllowed()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AUTHERIS_ALLOW_LEGACY_CONFIG"] = "true",
                ["Serilog:MinimumLevel"] = "Information",
                ["Gateway:Telemetry:Otlp:Headers:X-ApiKey"] = "secret123"
            })
            .Build();

        using var writer = new StringWriter();
        var ex = Should.Throw<InvalidOperationException>(() => SerilogSectionGuard.CheckConfiguration(config, writer));
        ex.Message.ShouldContain("SEC-OT06-14");
    }
}
