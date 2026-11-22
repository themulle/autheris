namespace Autheris.Tests.Unit.GraphQL;

using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Federation.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>VF-09: the outbound federation context signature covers the query string.</summary>
[Trait("Category", "Security")]
public sealed class SubgraphQuerySignatureVf09Tests
{
    private const string Secret = "super-secret-signing-key-for-subgraphs-123!";

    private static async Task<(string Sig, string Ts, string Nonce, string BodyHash)> SignAsync(string url)
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
        await service.ApplySecurityHeadersAsync(request, "products", user, "tenant_alpha");
        return (
            request.Headers.GetValues("X-Autheris-Signature").First(),
            request.Headers.GetValues("X-Autheris-Timestamp").First(),
            request.Headers.GetValues("X-Autheris-Nonce").First(),
            request.Headers.GetValues("X-Autheris-Body-Sha256").First());
    }

    private static bool MatchesCanonicalV2((string Sig, string Ts, string Nonce, string BodyHash) s, string pathAndQuery)
    {
        var canonical = FederationContextSignature.BuildCanonicalV2(
            tenant: "tenant_alpha",
            userSid: "S-1-5-21-FED-USER",
            timestamp: s.Ts,
            nonce: s.Nonce,
            subgraph: "products",
            roles: string.Empty,
            method: "GET",
            path: pathAndQuery,
            bodySha256: s.BodyHash);
        return string.Equals(s.Sig, FederationContextSignature.Sign(canonical, Secret), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedQueryString_Validates()
    {
        var signed = await SignAsync("https://subgraph.internal/graphql?query=%7Ba%7D");

        MatchesCanonicalV2(signed, "/graphql?query=%7Ba%7D").ShouldBeTrue();
    }

    [Fact]
    public async Task TamperedQueryString_IsRejected()
    {
        var signed = await SignAsync("https://subgraph.internal/graphql?query=%7Ba%7D");

        MatchesCanonicalV2(signed, "/graphql?query=%7Bevil%7D").ShouldBeFalse();
    }
}
