namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using Autheris.Domain.Options;
using Autheris.Application.Federation.Services;
using Autheris.GraphQL.Federation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>VF-09: the federation context signature covers the query string.</summary>
[Trait("Category", "Security")]
public sealed class SubgraphQuerySignatureVf09Tests
{
    private const string Secret = "super-secret-signing-key-for-subgraphs-123!";

    private static (string Sig, string Ts, string Nonce, string BodyHash) Sign(string url)
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
        var service = new SubgraphContextPropagationService(Options.Create(options), NullLogger<SubgraphContextPropagationService>.Instance);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-FED-USER")], "Test"));
        service.ApplySecurityHeaders(request, "products", user, "tenant_alpha");
        return (
            request.Headers.GetValues("X-Autheris-Signature").First(),
            request.Headers.GetValues("X-Autheris-Timestamp").First(),
            request.Headers.GetValues("X-Autheris-Nonce").First(),
            request.Headers.GetValues("X-Autheris-Body-Sha256").First());
    }

    private static bool Validate((string Sig, string Ts, string Nonce, string BodyHash) s, string pathAndQuery) =>
        SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_alpha",
            userSid: "S-1-5-21-FED-USER",
            timestampStr: s.Ts,
            nonce: s.Nonce,
            signature: s.Sig,
            signingKey: Secret,
            subgraph: "products",
            roles: "",
            method: "GET",
            path: pathAndQuery,
            version: "v2",
            bodySha256: s.BodyHash);

    [Fact]
    public void SignedQueryString_Validates()
    {
        var signed = Sign("https://subgraph.internal/graphql?query=%7Ba%7D");

        Validate(signed, "/graphql?query=%7Ba%7D").ShouldBeTrue();
    }

    [Fact]
    public void TamperedQueryString_IsRejected()
    {
        var signed = Sign("https://subgraph.internal/graphql?query=%7Ba%7D");

        Validate(signed, "/graphql?query=%7Bevil%7D").ShouldBeFalse();
    }
}
