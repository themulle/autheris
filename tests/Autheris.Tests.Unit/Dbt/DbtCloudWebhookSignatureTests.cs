namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Catalog.Semantic;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DbtCloudWebhookSignatureTests
{
    private const string Secret = "super-secret-dbt-token-12345";
    private const string Payload = """{"eventId":"evt_1","eventType":"job_run.completed"}""";

    internal static string Hex(string payload, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    [Fact]
    public void ValidateSignature_WithValidHmac_ReturnsTrue()
    {
        var signature = Hex(Payload, Secret);

        DbtCloudWebhookSignature.Verify(Payload, signature, Secret, bypass: false).ShouldBeTrue();
        DbtCloudWebhookSignature.Verify(Payload, "sha256=" + signature, Secret, bypass: false).ShouldBeTrue();
        DbtCloudWebhookSignature.Verify(Payload, signature.ToUpperInvariant(), Secret, bypass: false).ShouldBeTrue();
    }

    [Fact]
    public void ValidateSignature_WithInvalidHmac_ReturnsFalse()
    {
        var invalid = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        DbtCloudWebhookSignature.Verify(Payload, invalid, Secret, bypass: false).ShouldBeFalse();
        DbtCloudWebhookSignature.Verify(Payload, Hex(Payload, "other-secret"), Secret, bypass: false).ShouldBeFalse();
        DbtCloudWebhookSignature.Verify(Payload + " ", Hex(Payload, Secret), Secret, bypass: false).ShouldBeFalse();
    }

    [Fact]
    public void Invalid_or_missing_signature_header_is_rejected()
    {
        DbtCloudWebhookSignature.Verify(Payload, "invalid-sig", Secret, bypass: false).ShouldBeFalse();
        DbtCloudWebhookSignature.Verify(Payload, null, Secret, bypass: false).ShouldBeFalse();
        DbtCloudWebhookSignature.Verify(Payload, "  ", Secret, bypass: false).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Blank_secret_rejects_every_signature(string? secret)
    {
        DbtCloudWebhookSignature.Verify(Payload, Hex(Payload, string.Empty), secret, bypass: false).ShouldBeFalse();
        DbtCloudWebhookSignature.Verify(Payload, Hex(Payload, Secret), secret, bypass: false).ShouldBeFalse();
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Non_hex_or_wrong_length_value_is_rejected(string header)
    {
        DbtCloudWebhookSignature.Verify(Payload, header, Secret, bypass: false).ShouldBeFalse();
        DbtCloudWebhookSignature.Verify(Payload, "sha256=" + header, Secret, bypass: false).ShouldBeFalse();
    }

    [Fact]
    public void Prefix_is_rejected_when_not_allowed()
    {
        var signature = Hex(Payload, Secret);

        DbtCloudWebhookSignature.Verify(Payload, "sha256=" + signature, Secret, bypass: false, allowSha256Prefix: false).ShouldBeFalse();
        DbtCloudWebhookSignature.Verify(Payload, signature, Secret, bypass: false, allowSha256Prefix: false).ShouldBeTrue();
    }

    [Fact]
    public void Bypass_is_the_only_way_to_accept_an_unverified_payload()
    {
        DbtCloudWebhookSignature.Verify(Payload, "invalid", Secret, bypass: true).ShouldBeTrue();
        DbtCloudWebhookSignature.Verify(Payload, "invalid", string.Empty, bypass: true).ShouldBeTrue();
        DbtCloudWebhookSignature.Verify(Payload, "invalid", Secret, bypass: false).ShouldBeFalse();
    }
}
