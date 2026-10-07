using Autheris.Application.Services;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class ColumnMaskingTests
{
    private readonly ColumnMaskingProvider _provider = new();

    [Fact]
    public void MaskValue_WhenNull_ReturnsNull()
    {
        var rule = new MaskingRule { RuleType = "REDACT" };
        var result = _provider.MaskValue("any", null, rule);
        result.ShouldBeNull();
    }

    [Fact]
    public void MaskValue_RedactRule_ReturnsRedactedString()
    {
        var rule = new MaskingRule { RuleType = "REDACT", Replacement = "[CONFIDENTIAL]" };
        var result = _provider.MaskValue("secret_notes", "SuperSecretData", rule);
        result.ShouldBe("[CONFIDENTIAL]");
    }

    [Fact]
    public void MaskValue_NullifyRule_ReturnsNull()
    {
        var rule = new MaskingRule { RuleType = "NULLIFY" };
        var result = _provider.MaskValue("salary", 150000m, rule);
        result.ShouldBeNull();
    }

    [Fact]
    public void MaskValue_HmacSha256Rule_ReturnsDeterministic64CharHex()
    {
        var rule = new MaskingRule { RuleType = "HMAC" };
        var result1 = _provider.MaskValue("social_security_number", "123-45-6789", rule)?.ToString();
        var result2 = _provider.MaskValue("social_security_number", "123-45-6789", rule)?.ToString();
        var result3 = _provider.MaskValue("social_security_number", "987-65-4321", rule)?.ToString();

        result1.ShouldNotBeNull();
        result1.Length.ShouldBe(64); // 256 bits = 32 bytes = 64 hex characters
        result1.ShouldBe(result2); // Deterministic
        result1.ShouldNotBe(result3); // Different inputs yield different outputs
    }

    [Fact]
    public void MaskValue_RegexEmailFormat_MasksProperly()
    {
        var rule = new MaskingRule { RuleType = "REGEX" };
        var result = _provider.MaskValue("email_address", "john.doe@company.org", rule)?.ToString();

        result.ShouldNotBeNull();
        result.ShouldStartWith("j***@");
        result.ShouldEndWith(".org");
        result.ShouldNotContain("john.doe");
    }

    [Fact]
    public void MaskValue_RegexIbanFormat_MasksProperly()
    {
        var rule = new MaskingRule { RuleType = "REGEX" };
        var result = _provider.MaskValue("iban", "DE89370400440532013000", rule)?.ToString();

        result.ShouldNotBeNull();
        result.ShouldStartWith("DE**");
        result.ShouldEndWith("3000");
    }

    [Theory]
    [InlineData("123456", "1****6")]
    [InlineData("12345678", "1******8")]
    [InlineData("+491701234567", "+49*******567")]
    [InlineData("123", "***")]
    public void MaskValue_PhoneFormat_MasksProperlyWithoutExposingAllDigits(string rawPhone, string expectedMasked)
    {
        var rule = new MaskingRule { RuleType = "REGEX" };
        var result = _provider.MaskValue("phone_number", rawPhone, rule)?.ToString();

        result.ShouldBe(expectedMasked);
    }

    [Fact]
    public void ColumnMaskingProvider_ResolvesKeyFromKeyVaultSecretProvider()
    {
        // Finding A: Hmac key must be resolved from IKeyVaultSecretProvider, not used as raw UTF8
        var secretProvider = NSubstitute.Substitute.For<Autheris.Application.Interfaces.IKeyVaultSecretProvider>();
        var expectedBytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10 };
        secretProvider.GetSecretBytes("https://my-vault.vault.azure.net/secrets/hmac-key").Returns(expectedBytes);

        var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
        {
            DataMasking = new Autheris.Domain.Options.DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "https://my-vault.vault.azure.net/secrets/hmac-key"
            }
        });

        var provider = new ColumnMaskingProvider(options, secretProvider);

        var rule = new MaskingRule { RuleType = "HMAC" };
        var masked = provider.MaskValue("ssn", "123-45-6789", rule)?.ToString();

        masked.ShouldNotBeNull();
        masked.Length.ShouldBe(64);
        secretProvider.Received(1).GetSecretBytes("https://my-vault.vault.azure.net/secrets/hmac-key");
    }

    [Fact]
    public void ColumnMaskingProvider_NonDevelopmentWithDefaultFallback_ThrowsInvalidOperationException()
    {
        // Finding A / 23: When not in Development and key cannot be resolved or is fallback, must fail-fast
        var env = NSubstitute.Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Staging"); // Non-Development!

        var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
        {
            DataMasking = new Autheris.Domain.Options.DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "dev-only-hmac-salt-secure-fallback"
            }
        });

        Should.Throw<InvalidOperationException>(() =>
        {
            _ = new ColumnMaskingProvider(options, secretProvider: null, environment: env);
        });
    }

    [Fact]
    public void MaskValue_InvalidRegexPattern_ReturnsRedactedWithoutThrowing()
    {
        var rule = new MaskingRule
        {
            RuleType = "REGEX",
            PatternOrFormat = "[a-z", // Invalid unclosed character class
            Replacement = "X"
        };

        var result = _provider.MaskValue("custom_code", "abc123xyz", rule);
        result.ShouldBe("REDACTED");
    }

    [Fact]
    public void DefaultEnvironmentSecretProvider_WhenSecretRefIsKeyVaultUri_ResolvesSecretFromSecretName()
    {
        var config = NSubstitute.Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>();
        config["hmac-key"].Returns("SuperSecretHmacKeyValue123!AtLeast32Bytes");

        var env = NSubstitute.Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var secretProvider = new Autheris.Infrastructure.Security.DefaultEnvironmentSecretProvider(config, env);

        var secretBytes = secretProvider.GetSecretBytes("https://my-vault.vault.azure.net/secrets/hmac-key/v1");
        System.Text.Encoding.UTF8.GetString(secretBytes).ShouldBe("SuperSecretHmacKeyValue123!AtLeast32Bytes");
    }

    [Theory]
    [InlineData("john.doe@company.org", "j***@***.org")]
    [InlineData("alice@corp.internal.com", "a***@***.com")]
    [InlineData("bob@localhost", "b***@***")]
    [InlineData("c@short.com", "***@***")]
    [InlineData("@nodomain.com", "***@***")]
    [InlineData("no_at_sign", "***@***")]
    [InlineData("user@.com", "u***@***")]
    public void MaskValue_MaskEmail_ProducesExpectedResults(string rawEmail, string expected)
    {
        var rule = new MaskingRule { RuleType = "MASK_EMAIL" };
        var result = _provider.MaskValue("email", rawEmail, rule)?.ToString();
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("DE89370400440532013000", "DE** **** **** 3000")]
    [InlineData("DE89 3704 0044 0532 0130 00", "DE** **** **** 3000")]
    [InlineData("GB29 XAAA 0101 2345 6789 01", "GB** **** **** 8901")]
    [InlineData("SHORT", "****")]
    [InlineData("1234567", "****")]
    [InlineData("12345678", "12** **** **** 5678")]
    public void MaskValue_MaskIban_ProducesExpectedResults(string rawIban, string expected)
    {
        var rule = new MaskingRule { RuleType = "MASK_IBAN" };
        var result = _provider.MaskValue("iban", rawIban, rule)?.ToString();
        result.ShouldBe(expected);
    }

    [Fact]
    public void MaskValue_BinaryByteArray_HmacSha256Rule_ComputesHashOnRawBytes_NotTypeName()
    {
        var rule = new MaskingRule { RuleType = "HMAC" };
        var bytesA = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var bytesB = new byte[] { 0x05, 0x06, 0x07, 0x08 };

        var hashA = _provider.MaskValue("binary_col", bytesA, rule)?.ToString();
        var hashB = _provider.MaskValue("binary_col", bytesB, rule)?.ToString();
        var hashSystemByte = _provider.MaskValue("text_col", "System.Byte[]", rule)?.ToString();

        hashA.ShouldNotBeNull();
        hashB.ShouldNotBeNull();
        hashA.Length.ShouldBe(64);
        hashB.Length.ShouldBe(64);

        // SEC-SPEC-02: Different binary payloads must NOT collide on "System.Byte[]"
        hashA.ShouldNotBe(hashB);
        hashA.ShouldNotBe(hashSystemByte);
        hashB.ShouldNotBe(hashSystemByte);
    }

    [Fact]
    public void MaskValue_BinaryByteArray_RedactAndNullify_HandlesCleanly()
    {
        var redactRule = new MaskingRule { RuleType = "REDACT", Replacement = "[REDACTED_BLOB]" };
        var nullifyRule = new MaskingRule { RuleType = "NULLIFY" };
        var bytes = new byte[] { 0xAA, 0xBB, 0xCC };

        _provider.MaskValue("bin", bytes, redactRule).ShouldBe("[REDACTED_BLOB]");
        _provider.MaskValue("bin", bytes, nullifyRule).ShouldBeNull();
    }

    [Fact]
    public void MaskValue_Timestamps_CultureInvariantUtc_ProducesConsistentHmac()
    {
        var rule = new MaskingRule { RuleType = "HMAC" };
        var dtUtc = new DateTime(2026, 9, 30, 15, 30, 0, DateTimeKind.Utc);
        var dtUnspec = new DateTime(2026, 9, 30, 15, 30, 0, DateTimeKind.Unspecified);
        var dto = new DateTimeOffset(2026, 9, 30, 17, 30, 0, TimeSpan.FromHours(2)); // Same instant in UTC

        var hashUtc = _provider.MaskValue("ts", dtUtc, rule)?.ToString();
        var hashUnspec = _provider.MaskValue("ts", dtUnspec, rule)?.ToString();
        var hashDto = _provider.MaskValue("ts", dto, rule)?.ToString();

        hashUtc.ShouldNotBeNull();
        hashUtc.Length.ShouldBe(64);
        hashUtc.ShouldBe(hashUnspec);
        hashUtc.ShouldBe(hashDto);
    }
}


