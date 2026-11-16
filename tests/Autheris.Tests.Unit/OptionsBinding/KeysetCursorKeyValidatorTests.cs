namespace Autheris.Tests.Unit.OptionsBinding;

using System;
using System.Collections.Generic;
using Autheris.Api.Configuration.Validation;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class KeysetCursorKeyValidatorTests
{
    private const string ValidKey = "prod-keyset-cursor-signing-key-0123456789";

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

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Missing_key_outside_development_fails_startup_even_when_bulk_stream_disabled(string environment)
    {
        var validator = new BulkStreamSigningKeyOptionsValidator(Env(environment));

        var bulkDisabled = validator.Validate(null, OptionsWith(bulkEnabled: false, dataEndpointEnabled: false));
        bulkDisabled.Failed.ShouldBeTrue();
        bulkDisabled.Failures.ShouldHaveSingleItem().ShouldContain("Gateway:RestApi:BulkStream:SigningKey");

        var bothUnset = validator.Validate(null, new GatewayOptions());
        bothUnset.Failed.ShouldBeTrue();
        bothUnset.Failures.ShouldHaveSingleItem().ShouldContain("Gateway:RestApi:BulkStream:SigningKey");
    }

    [Fact]
    public void Short_key_fails_startup()
    {
        var validator = new BulkStreamSigningKeyOptionsValidator(Env("Production"));
        var shortKey = new string('k', 31);

        var result = validator.Validate(null, OptionsWith(shortKey, bulkEnabled: false));

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldHaveSingleItem().ShouldContain("at least 32 bytes");
        result.Failures.ShouldNotContain(f => f.Contains(shortKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \t\r\n  ")]
    [InlineData("    short-padded-key-under-32    ")]
    [InlineData("  0123456789012345678901234567890  ")] // 31 bytes after trim, 35 before trim
    [InlineData("changeme-changeme-changeme-changeme")]
    [InlineData("CHANGE-ME-PLEASE-SIGNING-KEY-VALUE")]
    [InlineData("placeholder-signing-key-32-bytes!!")]
    [InlineData("replace-with-a-real-32-byte-signing-key")]
    [InlineData("dev_insecure_signing_key_do_not_use_prod")]
    public void Whitespace_key_fails_startup(string invalidKey)
    {
        var validator = new BulkStreamSigningKeyOptionsValidator(Env("Production"));

        var result = validator.Validate(null, OptionsWith(invalidKey, bulkEnabled: false));

        result.Failed.ShouldBeTrue();
        var trimmed = invalidKey.Trim();
        if (trimmed.Length > 0)
        {
            result.Failures.ShouldNotContain(f => f.Contains(trimmed, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Valid_trimmed_key_outside_development_succeeds()
    {
        var validator = new BulkStreamSigningKeyOptionsValidator(Env("Production"));

        var result = validator.Validate(null, OptionsWith(ValidKey, bulkEnabled: false));

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Development_uses_ephemeral_key_with_warning()
    {
        var validatorLogger = new CapturingLogger<BulkStreamSigningKeyOptionsValidator>();
        var validator = new BulkStreamSigningKeyOptionsValidator(Env("Development"), validatorLogger);

        var validation = validator.Validate(null, OptionsWith(signingKey: null, bulkEnabled: false));

        validation.Succeeded.ShouldBeTrue();
        validatorLogger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("BulkStream:SigningKey", StringComparison.Ordinal));

        var encoderLogger = new CapturingLogger<KeysetCursorEncoder>();
        var encoder = new KeysetCursorEncoder(
            Options.Create(OptionsWith(signingKey: null)),
            Env("Development"),
            TimeProvider.System,
            encoderLogger);

        encoder.ShouldNotBeNull();
        encoderLogger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("BulkStream:SigningKey", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
