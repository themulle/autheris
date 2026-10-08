namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using Autheris.Application.Federation.Services;
using Autheris.Domain.Options;
using Autheris.GraphQL.Federation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class SubgraphFederationSecurityTests
{
    private const string Secret = "super-secret-signing-key-for-subgraphs-123!";

    [Fact]
    public void ApplySecurityHeaders_SignsContextHeaders_AndValidatorConfirmsSignature()
    {
        var options = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableZeroTrustContextForwarding = true,
                SignContextHeaders = true,
                SigningKey = Secret,
                TenantHeaderName = "X-Tenant-ID",
                SubjectHeaderName = "X-Gateway-Subject"
            }
        };

        var service = new SubgraphContextPropagationService(
            Options.Create(options),
            NullLogger<SubgraphContextPropagationService>.Instance);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.internal/graphql");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-FED-USER")], "Test"));

        service.ApplySecurityHeaders(request, "products", user, "tenant_alpha");

        // Verify headers are present
        request.Headers.Contains("X-Autheris-Signature").ShouldBeTrue();
        request.Headers.Contains("X-Autheris-Timestamp").ShouldBeTrue();
        request.Headers.Contains("X-Autheris-Nonce").ShouldBeTrue();

        var sig = request.Headers.GetValues("X-Autheris-Signature").First();
        var ts = request.Headers.GetValues("X-Autheris-Timestamp").First();
        var nonce = request.Headers.GetValues("X-Autheris-Nonce").First();

        // Validator should accept
        var isValid = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_alpha",
            userSid: "S-1-5-21-FED-USER",
            timestampStr: ts,
            nonce: nonce,
            signature: sig,
            signingKey: Secret);

        isValid.ShouldBeTrue();
    }

    [Fact]
    public void SubgraphSecurityValidator_TamperedTenant_ReturnsFalse()
    {
        var options = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableZeroTrustContextForwarding = true,
                SignContextHeaders = true,
                SigningKey = Secret
            }
        };

        var service = new SubgraphContextPropagationService(
            Options.Create(options),
            NullLogger<SubgraphContextPropagationService>.Instance);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.internal/graphql");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-FED-USER")], "Test"));

        service.ApplySecurityHeaders(request, "products", user, "tenant_alpha");

        var sig = request.Headers.GetValues("X-Autheris-Signature").First();
        var ts = request.Headers.GetValues("X-Autheris-Timestamp").First();
        var nonce = request.Headers.GetValues("X-Autheris-Nonce").First();

        // Tamper with tenant
        var isValid = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_EVIL",
            userSid: "S-1-5-21-FED-USER",
            timestampStr: ts,
            nonce: nonce,
            signature: sig,
            signingKey: Secret);

        isValid.ShouldBeFalse();
    }

    [Fact]
    public void SubgraphSecurityValidator_ExpiredTimestamp_ReturnsFalse()
    {
        var oldTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var payload = $"tenant_alpha:user1:{oldTimestamp}:{nonce}";

        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(Secret));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)));

        var isValid = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_alpha",
            userSid: "user1",
            timestampStr: oldTimestamp,
            nonce: nonce,
            signature: signature,
            signingKey: Secret,
            maxDrift: TimeSpan.FromSeconds(60));

        isValid.ShouldBeFalse();
    }
}
