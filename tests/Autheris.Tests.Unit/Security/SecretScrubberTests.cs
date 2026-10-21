namespace Autheris.Tests.Unit.Security;

using System;
using Autheris.Application.Security;
using Shouldly;
using Xunit;

public sealed class SecretScrubberTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Redact_NullOrWhitespace_ReturnsEmptyString(string? input)
    {
        SecretScrubber.Redact(input).ShouldBe(string.Empty);
        SecretScrubber.Scrub(input).ShouldBe(string.Empty);
    }

    [Fact]
    public void Redact_BearerToken_RedactsTokenValue()
    {
        var input = "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.t-ID";
        var result = SecretScrubber.Redact(input);

        result.ShouldBe("Authorization: Bearer ***REDACTED***");
    }

    [Theory]
    [InlineData("api_key: 'secret12345'", "api_key: ***REDACTED***")]
    [InlineData("api-key=\"secret12345\"", "api-key=***REDACTED***")]
    [InlineData("apikey: secret12345", "apikey: ***REDACTED***")]
    [InlineData("token = super_secret_token", "token = ***REDACTED***")]
    [InlineData("secret: 'my-pass'", "secret: ***REDACTED***")]
    [InlineData("password: \"top-secret\"", "password: ***REDACTED***")]
    [InlineData("pwd = my_password_value", "pwd = ***REDACTED***")]
    public void Redact_ApiKeyAndPasswords_RedactsValue(string input, string expected)
    {
        SecretScrubber.Redact(input).ShouldBe(expected);
    }

    [Fact]
    public void Redact_ConnectionStringPassword_RedactsPasswordValue()
    {
        var input = "Server=tcp:sql.db.windows.net;Database=governance;User Id=autheris;Password=SuperSecretPass123!;Encrypt=True;";
        var result = SecretScrubber.Redact(input);

        result.ShouldContain("Password=***REDACTED***;");
        result.ShouldNotContain("SuperSecretPass123!");
    }

    [Fact]
    public void Redact_AwsAccessKey_RedactsKey()
    {
        var input = "S3 credentials with AKIAIOSFODNN7EXAMPLE in request";
        var result = SecretScrubber.Redact(input);

        result.ShouldBe("S3 credentials with ***REDACTED*** in request");
        result.ShouldNotContain("AKIAIOSFODNN7EXAMPLE");
    }

    [Fact]
    public void Redact_CleanText_LeavesUnchanged()
    {
        var input = "SELECT id, name FROM hr.employees WHERE department = 'IT';";
        SecretScrubber.Redact(input).ShouldBe(input);
        SecretScrubber.Scrub(input).ShouldBe(input);
    }
}
