namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using Autheris.Application.Federation.Services;
using Autheris.Domain.Options;
using Autheris.GraphQL.Federation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
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
    public void ApplySecurityHeaders_OutsideDevelopment_WithDefaultSecret_ThrowsInvalidOperationException()
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

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns("Production");

        var service = new SubgraphContextPropagationService(
            Options.Create(options),
            NullLogger<SubgraphContextPropagationService>.Instance,
            prodEnv);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.internal/graphql");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-FED-USER")], "Test"));

        Should.Throw<InvalidOperationException>(() =>
            service.ApplySecurityHeaders(request, "products", user, "tenant_alpha"))
            .Message.ShouldContain("prohibited outside the Development environment");
    }

    [Fact]
    public void ApplySecurityHeaders_OutsideDevelopment_WithShortSecret_ThrowsInvalidOperationException()
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

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns("Production");

        var service = new SubgraphContextPropagationService(
            Options.Create(options),
            NullLogger<SubgraphContextPropagationService>.Instance,
            prodEnv);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.internal/graphql");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-FED-USER")], "Test"));

        Should.Throw<InvalidOperationException>(() =>
            service.ApplySecurityHeaders(request, "products", user, "tenant_alpha"))
            .Message.ShouldContain("must be at least 32 bytes long");
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

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns("Production");

        Should.Throw<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            Autheris.Api.Extensions.GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv))
            .Message.ShouldContain("Federation:SigningKey");
    }
}
