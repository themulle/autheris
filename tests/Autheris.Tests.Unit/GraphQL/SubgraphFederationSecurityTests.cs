#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.GraphQL;

using Autheris.Tests.Common;
using System;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Federation.Services;
using Autheris.Domain.Options;
using Autheris.GraphQL.Federation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class SubgraphFederationSecurityTests
{
    private const string Secret = "super-secret-signing-key-for-subgraphs-123!";

    [Fact]
    public async Task ApplySecurityHeaders_SignsContextHeaders_WithValidCanonicalV2Signature()
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

        await service.ApplySecurityHeadersAsync(request, "products", user, "tenant_alpha");

        // Verify outbound Zero-Trust headers and canonical v2 HMAC signature
        request.Headers.Contains("X-Autheris-Signature").ShouldBeTrue();
        request.Headers.Contains("X-Autheris-Timestamp").ShouldBeTrue();
        request.Headers.Contains("X-Autheris-Nonce").ShouldBeTrue();
        request.Headers.GetValues(FederationContextSignature.VersionHeader).Single().ShouldBe(FederationContextSignature.CurrentVersion);

        var sig = request.Headers.GetValues("X-Autheris-Signature").First();
        var ts = request.Headers.GetValues("X-Autheris-Timestamp").First();
        var nonce = request.Headers.GetValues("X-Autheris-Nonce").First();
        var bodyHash = request.Headers.GetValues("X-Autheris-Body-Sha256").First();

        var expectedCanonical = FederationContextSignature.BuildCanonicalV2(
            "tenant_alpha",
            "S-1-5-21-FED-USER",
            ts,
            nonce,
            "products",
            string.Empty,
            "POST",
            "/graphql",
            bodyHash);
        sig.ShouldBe(FederationContextSignature.Sign(expectedCanonical, Secret));
    }

    private static async Task<(HttpRequestMessage Request, string Sig, string Ts, string Nonce, string BodyHash)> SignedRequestAsync(HttpContent? content, string roleA = "B", string roleB = "A")
    {
        var options = new GatewayOptions
        {
            Federation = new FederationOptions { Enabled = true, EnableZeroTrustContextForwarding = true, SignContextHeaders = true, SigningKey = Secret }
        };
        var service = new SubgraphContextPropagationService(Options.Create(options), NullLogger<SubgraphContextPropagationService>.Instance);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.internal/graphql") { Content = content };
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1"), new Claim(ClaimTypes.Role, roleA), new Claim(ClaimTypes.Role, roleB)], "Test"));
        await service.ApplySecurityHeadersAsync(request, "products", user, "tenant_alpha");
        var sig = request.Headers.GetValues("X-Autheris-Signature").First();
        var ts = request.Headers.GetValues("X-Autheris-Timestamp").First();
        var nonce = request.Headers.GetValues("X-Autheris-Nonce").First();
        var bodyHash = request.Headers.GetValues("X-Autheris-Body-Sha256").First();
        return (request, sig, ts, nonce, bodyHash);
    }

    [Fact]
    public async Task SR_M_18_BodyIsBoundToSignature()
    {
        var (request, sig, ts, nonce, bodyHash) = await SignedRequestAsync(new StringContent("{\"query\":\"{a}\"}"));
        var expected = FederationContextSignature.HashBody(System.Text.Encoding.UTF8.GetBytes("{\"query\":\"{a}\"}"));
        bodyHash.ShouldBe(expected);
        var roles = string.Join(",", request.Headers.GetValues("X-Gateway-Roles"));

        var genuineCanonical = FederationContextSignature.BuildCanonicalV2(
            "tenant_alpha", "S-1", ts, nonce, "products", roles, "POST", "/graphql", bodyHash);
        sig.ShouldBe(FederationContextSignature.Sign(genuineCanonical, Secret));

        var tamperedHash = FederationContextSignature.HashBody(System.Text.Encoding.UTF8.GetBytes("{\"query\":\"{evil}\"}"));
        var tamperedCanonical = FederationContextSignature.BuildCanonicalV2(
            "tenant_alpha", "S-1", ts, nonce, "products", roles, "POST", "/graphql", tamperedHash);
        sig.ShouldNotBe(FederationContextSignature.Sign(tamperedCanonical, Secret));
    }

    [Fact]
    public async Task SR_M_18_RolesHeaderIsSortedCanonical_AndVersionHeaderIsV2()
    {
        var (request, sig, ts, nonce, bodyHash) = await SignedRequestAsync(null);
        var roles = string.Join(",", request.Headers.GetValues("X-Gateway-Roles"));
        roles.ShouldBe("A,B");
        request.Headers.GetValues(FederationContextSignature.VersionHeader).Single().ShouldBe("v2");

        var canonical = FederationContextSignature.BuildCanonicalV2(
            "tenant_alpha", "S-1", ts, nonce, "products", roles, "POST", "/graphql", bodyHash);
        sig.ShouldBe(FederationContextSignature.Sign(canonical, Secret));
    }

    [Fact]
    public void SR_M_18_CanonicalForm_IsUnambiguous()
    {
        var a = FederationContextSignature.BuildCanonicalV2("t:u", "x", "1", "n", "s", "", "POST", "/p", "h");
        var b = FederationContextSignature.BuildCanonicalV2("t", "u:x", "1", "n", "s", "", "POST", "/p", "h");
        a.ShouldNotBe(b);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task SubgraphDelegatingHandler_Default_DoesNotForwardBearerToken()
    {
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_allow_insecure_transport = true },
            Federation = new FederationOptions
            {
                Enabled = true,
                SigningKey = Secret,
                Subgraphs = [new SubgraphEndpointOptions { Name = "products", Url = "https://products.internal/graphql", ForwardClientBearerToken = false }]
            }
        };

        var propService = new SubgraphContextPropagationService(Options.Create(options), NullLogger<SubgraphContextPropagationService>.Instance);
        var httpContextAccessor = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };
        httpContextAccessor.HttpContext.Request.Headers["Authorization"] = "Bearer client-secret-token";

        var capturing = new CapturingHandler();
        var handler = new SubgraphSecurityDelegatingHandler(
            "products",
            propService,
            httpContextAccessor,
            NullLogger<SubgraphSecurityDelegatingHandler>.Instance,
            Options.Create(options))
        {
            InnerHandler = capturing
        };

        var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://products.internal/graphql");

        await client.SendAsync(request);

        capturing.CapturedRequest.ShouldNotBeNull();
        capturing.CapturedRequest.Headers.Contains("Authorization").ShouldBeFalse();
    }

    [Fact]
    public async Task SubgraphDelegatingHandler_McpRequest_NeverForwardsBearerToken()
    {
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_allow_insecure_transport = true },
            Federation = new FederationOptions
            {
                Enabled = true,
                SigningKey = Secret,
                Subgraphs = [new SubgraphEndpointOptions { Name = "products", Url = "https://products.internal/graphql", ForwardClientBearerToken = true }]
            }
        };

        var propService = new SubgraphContextPropagationService(Options.Create(options), NullLogger<SubgraphContextPropagationService>.Instance);
        var httpContextAccessor = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };
        httpContextAccessor.HttpContext.Request.Headers["Authorization"] = "Bearer mcp-secret-token";
        httpContextAccessor.HttpContext.Items["IsMcpRequest"] = true;

        var capturing = new CapturingHandler();
        var handler = new SubgraphSecurityDelegatingHandler(
            "products",
            propService,
            httpContextAccessor,
            NullLogger<SubgraphSecurityDelegatingHandler>.Instance,
            Options.Create(options))
        {
            InnerHandler = capturing
        };

        var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://products.internal/graphql");

        await client.SendAsync(request);

        capturing.CapturedRequest.ShouldNotBeNull();
        capturing.CapturedRequest.Headers.Contains("Authorization").ShouldBeFalse();
    }

    [Fact]
    public async Task SubgraphDelegatingHandler_WhenExplicitlyAllowed_ForwardsBearerToken()
    {
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_allow_insecure_transport = true },
            Federation = new FederationOptions
            {
                Enabled = true,
                SigningKey = Secret,
                Subgraphs = [new SubgraphEndpointOptions { Name = "products", Url = "https://products.internal/graphql", ForwardClientBearerToken = true }]
            }
        };

        var propService = new SubgraphContextPropagationService(Options.Create(options), NullLogger<SubgraphContextPropagationService>.Instance);
        var httpContextAccessor = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };
        httpContextAccessor.HttpContext.Request.Headers["Authorization"] = "Bearer trusted-client-token";

        var capturing = new CapturingHandler();
        var handler = new SubgraphSecurityDelegatingHandler(
            "products",
            propService,
            httpContextAccessor,
            NullLogger<SubgraphSecurityDelegatingHandler>.Instance,
            Options.Create(options))
        {
            InnerHandler = capturing
        };

        var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://products.internal/graphql");

        await client.SendAsync(request);

        capturing.CapturedRequest.ShouldNotBeNull();
        capturing.CapturedRequest.Headers.Contains("Authorization").ShouldBeTrue();
        capturing.CapturedRequest.Headers.GetValues("Authorization").First().ShouldBe("Bearer trusted-client-token");
    }

    [Fact]
    public async Task ApplySecurityHeaders_OutsideDevelopment_WithDefaultSecret_ThrowsInvalidOperationException()
    {
        var options = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableZeroTrustContextForwarding = true,
                SignContextHeaders = true,
                SigningKey = "autheris-federation-default-secret"
            }
        };

        IHostEnvironment prodEnv = new TestHostEnvironment("Production");

        var service = new SubgraphContextPropagationService(
            Options.Create(options),
            NullLogger<SubgraphContextPropagationService>.Instance,
            prodEnv);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.internal/graphql");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-FED-USER")], "Test"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            service.ApplySecurityHeadersAsync(request, "products", user, "tenant_alpha"));
        ex.Message.ShouldContain("prohibited outside the Development environment");
    }

    [Fact]
    public async Task ApplySecurityHeaders_OutsideDevelopment_WithShortSecret_ThrowsInvalidOperationException()
    {
        var options = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableZeroTrustContextForwarding = true,
                SignContextHeaders = true,
                SigningKey = "too-short-secret" // less than 32 bytes
            }
        };

        IHostEnvironment prodEnv = new TestHostEnvironment("Production");

        var service = new SubgraphContextPropagationService(
            Options.Create(options),
            NullLogger<SubgraphContextPropagationService>.Instance,
            prodEnv);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.internal/graphql");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-FED-USER")], "Test"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            service.ApplySecurityHeadersAsync(request, "products", user, "tenant_alpha"));
        ex.Message.ShouldContain("must be at least 32 bytes long");
    }

    [Fact]
    public void ValidateGatewayOptions_OutsideDevelopment_WithDefaultOrMissingSigningKey_ThrowsValidationException()
    {
        var options = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                SignContextHeaders = true,
                SigningKey = "autheris-federation-default-secret"
            }
        };

        IHostEnvironment prodEnv = new TestHostEnvironment("Production");

        Should.Throw<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            Autheris.Api.Extensions.GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv))
            .Message.ShouldContain("Federation:SigningKey");
    }
}
