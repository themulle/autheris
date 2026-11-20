namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Catalog.OpenMetadata;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class OpenMetadataWebhookSignatureTests
{
    private const string Secret = "my-om-secret-for-unit-tests";
    private const string Payload = """{"eventType":"entityUpdated","entityType":"table"}""";

    private static string Hex(string payload, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    [Fact]
    [Trait("Category", "Security")]
    public void Valid_hmac_with_and_without_sha256_prefix_accepted()
    {
        var validHex = Hex(Payload, Secret);

        OpenMetadataWebhookSignature.Verify(Payload, validHex, Secret, bypass: false).ShouldBeTrue();
        OpenMetadataWebhookSignature.Verify(Payload, $"sha256={validHex}", Secret, bypass: false).ShouldBeTrue();
        OpenMetadataWebhookSignature.Verify(Payload, validHex.ToUpperInvariant(), Secret, bypass: false).ShouldBeTrue();
        OpenMetadataWebhookSignature.Verify(Payload + " ", validHex, Secret, bypass: false).ShouldBeFalse();
        OpenMetadataWebhookSignature.Verify(Payload, validHex, "wrong-secret", bypass: false).ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("invalid_sig")]
    [InlineData("zz")]
    [InlineData("abcdef")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Non_hex_or_wrong_length_signature_rejected(string? signature)
    {
        OpenMetadataWebhookSignature.Verify(Payload, signature, Secret, bypass: false).ShouldBeFalse();
        if (signature is not null)
        {
            OpenMetadataWebhookSignature.Verify(Payload, "sha256=" + signature, Secret, bypass: false).ShouldBeFalse();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Literal_bypassed_signature_is_rejected_when_bypass_off()
    {
        OpenMetadataWebhookSignature.Verify("{}", "bypassed", Secret, bypass: false).ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_secret_returns_false(string? secret)
    {
        OpenMetadataWebhookSignature.Verify(Payload, Hex(Payload, string.Empty), secret, bypass: false).ShouldBeFalse();
        OpenMetadataWebhookSignature.Verify(Payload, Hex(Payload, Secret), secret, bypass: false).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Exception_returns_false_without_secret_in_log()
    {
        var logger = new CapturingLogger();
        const string sensitiveSecret = "super-sensitive-om-secret-999";
        var validHex = Hex(Payload, sensitiveSecret);

        var ok = OpenMetadataWebhookSignature.Verify(null!, validHex, sensitiveSecret, bypass: false, logger);

        ok.ShouldBeFalse();
        logger.Entries.ShouldNotBeEmpty();
        var logText = string.Join('\n', logger.Entries);
        logText.ShouldContain(nameof(ArgumentNullException));
        logText.ShouldNotContain(sensitiveSecret);
        logText.ShouldNotContain(validHex);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception) + exception);
    }
}
