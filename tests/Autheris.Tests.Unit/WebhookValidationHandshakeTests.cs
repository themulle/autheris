using Autheris.Api.Endpoints;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>EP-14: a malformed EventGrid handshake body is rejected without throwing on the anonymous webhook routes.</summary>
public sealed class WebhookValidationHandshakeTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("[1]-")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public void MalformedOrScalarPayload_IsInvalid_EP14(string payload) =>
        WebhookEndpoints.TryReadValidationCode(payload, out _).ShouldBeFalse();

    [Theory]
    [InlineData("[{\"data\":{\"validationCode\":\"abc\"}}]", "abc")]
    [InlineData("{\"data\":{\"validationCode\":\"xyz\"}}", "xyz")]
    [InlineData("[1]", null)]
    [InlineData("{\"data\":{\"validationCode\":5}}", null)]
    [InlineData("{\"data\":[]}", null)]
    public void WellFormedPayload_ReturnsCodeOrNull_EP14(string payload, string? expected)
    {
        WebhookEndpoints.TryReadValidationCode(payload, out var code).ShouldBeTrue();
        code.ShouldBe(expected);
    }
}
