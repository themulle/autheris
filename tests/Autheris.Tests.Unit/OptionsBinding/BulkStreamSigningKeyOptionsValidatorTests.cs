namespace Autheris.Tests.Unit.OptionsBinding;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Outside Development the keyset cursor signing key (<c>Gateway:RestApi:BulkStream:SigningKey</c>) must be configured while the bulk
/// stream endpoints are enabled: without it every node signs with its own random per-process key and multi-node continuations fail.
/// </summary>
public sealed class BulkStreamSigningKeyOptionsValidatorTests
{
    private const string StrongKey = "0123456789012345678901234567890123456789";

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static GatewayOptions OptionsWith(string? signingKey = null, bool? bulkEnabled = null, bool? dataEndpointEnabled = null) => new()
    {
        RestApi = new RestApiOptions
        {
            BulkStream = new BulkStreamOptions { Enabled = bulkEnabled, SigningKey = signingKey },
            DataEndpoint = new RestDataEndpointOptions { Enabled = dataEndpointEnabled ?? true }
        }
    };

    private static ValidateOptionsResult Run(GatewayOptions options, string environment, ILogger<BulkStreamSigningKeyOptionsValidator>? logger = null) =>
        new BulkStreamSigningKeyOptionsValidator(Env(environment), logger).Validate(null, options);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void MissingKey_OutsideDevelopment_FailsTheStart(string environment)
    {
        var result = Run(OptionsWith(), environment);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldHaveSingleItem().ShouldContain("Gateway:RestApi:BulkStream:SigningKey");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankKey_OutsideDevelopment_FailsTheStart(string key)
    {
        Run(OptionsWith(key), "Production").Failed.ShouldBeTrue();
    }

    [Fact]
    public void ShortKey_OutsideDevelopment_FailsTheStart_WithoutEchoingTheKey()
    {
        var shortKey = new string('s', 31);

        var result = Run(OptionsWith(shortKey), "Production");

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldHaveSingleItem().ShouldContain("at least 32 bytes");
        result.Failures.ShouldNotContain(f => f.Contains(shortKey, StringComparison.Ordinal));
    }

    [Fact]
    public void KeyOf32Bytes_OutsideDevelopment_Passes()
    {
        Run(OptionsWith(new string('k', 32)), "Production").Succeeded.ShouldBeTrue();
        Run(OptionsWith(StrongKey), "Production").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void MultiByteKey_IsMeasuredInBytes_NotCharacters()
    {
        // 16 characters, 32 UTF-8 bytes.
        Run(OptionsWith(new string('ä', 16)), "Production").Succeeded.ShouldBeTrue();
        // 16 characters, 16 bytes.
        Run(OptionsWith(new string('k', 16)), "Production").Failed.ShouldBeTrue();
    }

    [Fact]
    public void MissingKey_OutsideDevelopment_PassesWhenBulkStreamIsDisabled()
    {
        Run(OptionsWith(bulkEnabled: false), "Production").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void MissingKey_OutsideDevelopment_PassesWhenRestDataEndpointIsDisabled_AndBulkSwitchIsUnset()
    {
        // The bulk stream switch follows the REST data endpoint switch when it is not set itself.
        Run(OptionsWith(dataEndpointEnabled: false), "Production").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void MissingKey_OutsideDevelopment_FailsWhenBulkStreamIsExplicitlyEnabled_EvenIfRestDataEndpointIsDisabled()
    {
        Run(OptionsWith(bulkEnabled: true, dataEndpointEnabled: false), "Production").Failed.ShouldBeTrue();
    }

    [Fact]
    public void MissingKey_InDevelopment_PassesWithAWarning()
    {
        var logger = new CapturingLogger();

        Run(OptionsWith(), "Development", logger).Succeeded.ShouldBeTrue();

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("BulkStream:SigningKey", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfiguredKey_InDevelopment_PassesWithoutWarning_EvenWhenShort()
    {
        var logger = new CapturingLogger();

        Run(OptionsWith("dev"), "Development", logger).Succeeded.ShouldBeTrue();

        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void MissingKey_InDevelopment_PassesWithoutALogger()
    {
        Run(OptionsWith(), "Development").Succeeded.ShouldBeTrue();
    }

    private sealed class CapturingLogger : ILogger<BulkStreamSigningKeyOptionsValidator>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
