#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.GraphQL;

using Autheris.Tests.Common;
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
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

[Collection("SubgraphReplayCache")]
[Trait("Category", "Security")]
public sealed class SubgraphFederationSecurityTests
{
    private const string Secret = "super-secret-signing-key-for-subgraphs-123!";

    [Fact]
    public async Task ApplySecurityHeaders_SignsContextHeaders_AndValidatorConfirmsSignature()
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

        // Verify headers are present
        request.Headers.Contains("X-Autheris-Signature").ShouldBeTrue();
        request.Headers.Contains("X-Autheris-Timestamp").ShouldBeTrue();
        request.Headers.Contains("X-Autheris-Nonce").ShouldBeTrue();

        var sig = request.Headers.GetValues("X-Autheris-Signature").First();
        var ts = request.Headers.GetValues("X-Autheris-Timestamp").First();
        var nonce = request.Headers.GetValues("X-Autheris-Nonce").First();
        var bodyHash = request.Headers.GetValues("X-Autheris-Body-Sha256").First();

        // Validator should accept
        var isValid = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_alpha",
            userSid: "S-1-5-21-FED-USER",
            timestampStr: ts,
            nonce: nonce,
            signature: sig,
            signingKey: Secret,
            subgraph: "products",
            roles: "",
            method: "POST",
            path: "/graphql",
            version: "v2",
            bodySha256: bodyHash);

        isValid.ShouldBeTrue();
    }

    [Fact]
    public async Task SubgraphSecurityValidator_TamperedTenant_ReturnsFalse()
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

        await service.ApplySecurityHeadersAsync(request, "products", user, "tenant_alpha");

        var sig = request.Headers.GetValues("X-Autheris-Signature").First();
        var ts = request.Headers.GetValues("X-Autheris-Timestamp").First();
        var nonce = request.Headers.GetValues("X-Autheris-Nonce").First();
        var bodyHash = request.Headers.GetValues("X-Autheris-Body-Sha256").First();

        // Tamper with tenant
        var isValid = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_EVIL",
            userSid: "S-1-5-21-FED-USER",
            timestampStr: ts,
            nonce: nonce,
            signature: sig,
            signingKey: Secret,
            subgraph: "products",
            roles: "",
            method: "POST",
            path: "/graphql",
            version: "v2",
            bodySha256: bodyHash);

        isValid.ShouldBeFalse();
    }

    [Fact]
    public async Task SubgraphSecurityValidator_TamperedRoles_ReturnsFalse()
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
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-FED-USER"),
            new Claim(ClaimTypes.Role, "Reader")
        ], "Test"));

        await service.ApplySecurityHeadersAsync(request, "products", user, "tenant_alpha");

        var sig = request.Headers.GetValues("X-Autheris-Signature").First();
        var ts = request.Headers.GetValues("X-Autheris-Timestamp").First();
        var nonce = request.Headers.GetValues("X-Autheris-Nonce").First();
        var bodyHash = request.Headers.GetValues("X-Autheris-Body-Sha256").First();

        // Tamper with roles by escalating to ClusterAdmin
        var isValid = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_alpha",
            userSid: "S-1-5-21-FED-USER",
            timestampStr: ts,
            nonce: nonce,
            signature: sig,
            signingKey: Secret,
            subgraph: "products",
            roles: "ClusterAdmin",
            method: "POST",
            path: "/graphql",
            version: "v2",
            bodySha256: bodyHash);

        isValid.ShouldBeFalse();
    }

    [Fact]
    public async Task SubgraphSecurityValidator_TamperedSubgraph_ReturnsFalse()
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

        await service.ApplySecurityHeadersAsync(request, "products", user, "tenant_alpha");

        var sig = request.Headers.GetValues("X-Autheris-Signature").First();
        var ts = request.Headers.GetValues("X-Autheris-Timestamp").First();
        var nonce = request.Headers.GetValues("X-Autheris-Nonce").First();
        var bodyHash = request.Headers.GetValues("X-Autheris-Body-Sha256").First();

        // Replay to another subgraph (e.g. orders)
        var isValid = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_alpha",
            userSid: "S-1-5-21-FED-USER",
            timestampStr: ts,
            nonce: nonce,
            signature: sig,
            signingKey: Secret,
            subgraph: "orders",
            roles: "",
            method: "POST",
            path: "/graphql",
            version: "v2",
            bodySha256: bodyHash);

        isValid.ShouldBeFalse();
    }

    [Fact]
    public void SubgraphSecurityValidator_ExpiredTimestamp_ReturnsFalse()
    {
        var oldTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var payload = Autheris.Application.Federation.Services.FederationContextSignature.BuildCanonicalV2("tenant_alpha", "user1", oldTimestamp, nonce, "products", "", "POST", "/graphql", "00");

        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(Secret));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)));

        var isValid = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_alpha",
            userSid: "user1",
            timestampStr: oldTimestamp,
            nonce: nonce,
            signature: signature,
            signingKey: Secret,
            maxDrift: TimeSpan.FromSeconds(60),
            subgraph: "products",
            roles: "",
            method: "POST",
            path: "/graphql",
            version: "v2",
            bodySha256: "00");

        isValid.ShouldBeFalse();
    }

    [Fact]
    public void SubgraphSecurityValidator_ReusedNonce_ReturnsFalseOnReplay()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = "nonce-" + Guid.NewGuid().ToString("N");
        var payload = Autheris.Application.Federation.Services.FederationContextSignature.BuildCanonicalV2("tenant_alpha", "user1", timestamp, nonce, "products", "", "POST", "/graphql", "00");
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(Secret));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)));

        var firstResult = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_alpha",
            userSid: "user1",
            timestampStr: timestamp,
            nonce: nonce,
            signature: signature,
            signingKey: Secret,
            subgraph: "products",
            roles: "",
            method: "POST",
            path: "/graphql",
            version: "v2",
            bodySha256: "00");

        firstResult.ShouldBeTrue();

        // Second submission with exact same nonce within clock drift must fail closed as replay attack
        var replayResult = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_alpha",
            userSid: "user1",
            timestampStr: timestamp,
            nonce: nonce,
            signature: signature,
            signingKey: Secret,
            subgraph: "products",
            roles: "",
            method: "POST",
            path: "/graphql",
            version: "v2",
            bodySha256: "00");

        replayResult.ShouldBeFalse();
    }

    [Fact]
    public void SubgraphSecurityValidator_MissingSubgraph_RejectsSignatureDowngrade()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = "nonce-" + Guid.NewGuid().ToString("N");
        var payload = $"tenant_alpha:user1:{timestamp}:{nonce}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(Secret));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)));

        // Attempting to validate a 4-part signature without target subgraph binding must be rejected
        var isValid = SubgraphSecurityValidator.ValidateSignature(
            tenant: "tenant_alpha",
            userSid: "user1",
            timestampStr: timestamp,
            nonce: nonce,
            signature: signature,
            signingKey: Secret,
            subgraph: null);

        isValid.ShouldBeFalse();
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
        var expected = Autheris.Application.Federation.Services.FederationContextSignature.HashBody(System.Text.Encoding.UTF8.GetBytes("{\"query\":\"{a}\"}"));
        bodyHash.ShouldBe(expected);
        var roles = string.Join(",", request.Headers.GetValues("X-Gateway-Roles"));

        // Tampered body hash is rejected
        SubgraphSecurityValidator.ValidateSignature("tenant_alpha", "S-1", ts, nonce, sig, Secret, subgraph: "products", roles: roles,
            method: "POST", path: "/graphql", version: "v2",
            bodySha256: Autheris.Application.Federation.Services.FederationContextSignature.HashBody(System.Text.Encoding.UTF8.GetBytes("{\"query\":\"{evil}\"}"))).ShouldBeFalse();

        // Genuine one is accepted
        SubgraphSecurityValidator.ValidateSignature("tenant_alpha", "S-1", ts, nonce, sig, Secret, subgraph: "products", roles: roles,
            method: "POST", path: "/graphql", version: "v2", bodySha256: bodyHash).ShouldBeTrue();
    }

    [Fact]
    public async Task SR_M_18_RolesHeaderIsSortedCanonical_AndLegacyOrMissingVersionRejected()
    {
        var (request, sig, ts, nonce, bodyHash) = await SignedRequestAsync(null);
        var roles = string.Join(",", request.Headers.GetValues("X-Gateway-Roles"));
        roles.ShouldBe("A,B");

        SubgraphSecurityValidator.ValidateSignature("tenant_alpha", "S-1", ts, nonce, sig, Secret, subgraph: "products", roles: roles,
            method: "POST", path: "/graphql", version: null, bodySha256: bodyHash).ShouldBeFalse();
        SubgraphSecurityValidator.ValidateSignature("tenant_alpha", "S-1", ts, nonce, sig, Secret, subgraph: "products", roles: roles,
            method: "POST", path: "/graphql", version: "v2", bodySha256: bodyHash).ShouldBeTrue();
    }

    [Fact]
    public void SR_M_18_CanonicalForm_IsUnambiguous()
    {
        var a = Autheris.Application.Federation.Services.FederationContextSignature.BuildCanonicalV2("t:u", "x", "1", "n", "s", "", "POST", "/p", "h");
        var b = Autheris.Application.Federation.Services.FederationContextSignature.BuildCanonicalV2("t", "u:x", "1", "n", "s", "", "POST", "/p", "h");
        a.ShouldNotBe(b);
    }

    [Fact]
    public async Task SR_M_18_ReplayDetectedViaDistributedClusterState()
    {
        SubgraphSecurityValidator.ResetReplayCache();
        var (request, sig, ts, nonce, bodyHash) = await SignedRequestAsync(null);
        var roles = string.Join(",", request.Headers.GetValues("X-Gateway-Roles"));
        var cluster = NSubstitute.Substitute.For<Autheris.Application.State.IDistributedClusterStateProvider>();
        cluster.IncrementAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<long?>(2L)); // another node already saw this nonce

        var ok = await SubgraphSecurityValidator.ValidateSignatureAsync("tenant_alpha", "S-1", ts, nonce, sig, Secret, cluster,
            subgraph: "products", roles: roles, method: "POST", path: "/graphql", version: "v2", bodySha256: bodyHash);
        ok.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ReplayCheck_WhenClusterStateUnavailable_FailsClosedOnlyWhenClusterWide(bool clusterWide, bool expectedOk)
    {
        SubgraphSecurityValidator.ResetReplayCache();
        var (request, sig, ts, nonce, bodyHash) = await SignedRequestAsync(null);
        var roles = string.Join(",", request.Headers.GetValues("X-Gateway-Roles"));
        var cluster = NSubstitute.Substitute.For<Autheris.Application.State.IDistributedClusterStateProvider>();
        cluster.IncrementAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<long?>((long?)null)); // store unavailable
        var topology = new Autheris.Application.State.ClusterStateTopology(
            clusterWide ? Autheris.Application.State.ClusterStateMode.External : Autheris.Application.State.ClusterStateMode.InMemory);

        var ok = await SubgraphSecurityValidator.ValidateSignatureAsync("tenant_alpha", "S-1", ts, nonce, sig, Secret, cluster,
            subgraph: "products", roles: roles, method: "POST", path: "/graphql", version: "v2", bodySha256: bodyHash, topology: topology);

        ok.ShouldBe(expectedOk);
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    [InlineData("99999999999999")]
    public void SR_L_17_OutOfRangeTimestamp_ReturnsFalseWithoutThrowing(string timestamp)
    {
        SubgraphSecurityValidator.ValidateSignature("tenant_alpha", "S-1", timestamp, "n1", "abc", Secret, subgraph: "products",
            version: "v2", bodySha256: "00").ShouldBeFalse();
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

    [Fact]
    public void ValidateSignature_PrunesExpiredNoncesByMaxDrift_AndBoundsReplayCacheCapacity()
    {
        var start = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(start);
        SubgraphSecurityValidator.TimeProvider = fakeTime;
        SubgraphSecurityValidator.ResetReplayCache();
        try
        {
            const string bodySha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            static string Sign(DateTimeOffset ts, string tenant, string nonce)
            {
                var p = FederationContextSignature.BuildCanonicalV2(tenant, "user_1", ts.ToUnixTimeSeconds().ToString(), nonce, "products", "", "POST", "/graphql", bodySha);
                return FederationContextSignature.Sign(p, Secret);
            }

            // Long-drift (> 10 min) entry must stay cached past 10 minutes and block replay
            var longTs = fakeTime.GetUtcNow();
            var longSig = Sign(longTs, "tenant_drift", "long_nonce");
            SubgraphSecurityValidator.ValidateSignature("tenant_drift", "user_1", longTs.ToUnixTimeSeconds().ToString(), "long_nonce", longSig, Secret, TimeSpan.FromMinutes(15), "products", "", "POST", "/graphql", "v2", bodySha).ShouldBeTrue();

            fakeTime.Advance(TimeSpan.FromMinutes(11));
            SubgraphSecurityValidator.ValidateSignature("tenant_drift", "user_1", longTs.ToUnixTimeSeconds().ToString(), "long_nonce", longSig, Secret, TimeSpan.FromMinutes(15), "products", "", "POST", "/graphql", "v2", bodySha).ShouldBeFalse();

            // Short-drift entry expiring before long_nonce must still be pruned despite long_nonce staying valid
            var shortTs = fakeTime.GetUtcNow();
            SubgraphSecurityValidator.ValidateSignature("tenant_drift", "user_1", shortTs.ToUnixTimeSeconds().ToString(), "short_nonce", Sign(shortTs, "tenant_drift", "short_nonce"), Secret, TimeSpan.FromSeconds(5), "products", "", "POST", "/graphql", "v2", bodySha).ShouldBeTrue();
            SubgraphSecurityValidator.CachedNonceCount.ShouldBe(2);

            fakeTime.Advance(TimeSpan.FromSeconds(6));
            var triggerTs = fakeTime.GetUtcNow();
            SubgraphSecurityValidator.ValidateSignature("tenant_drift", "user_1", triggerTs.ToUnixTimeSeconds().ToString(), "trigger_nonce", Sign(triggerTs, "tenant_drift", "trigger_nonce"), Secret, TimeSpan.FromSeconds(60), "products", "", "POST", "/graphql", "v2", bodySha).ShouldBeTrue();
            SubgraphSecurityValidator.CachedNonceCount.ShouldBe(2);

            // Heap and dictionary stay bounded even when long_nonce precedes > 20k short-drift entries
            for (int i = 0; i < 20_500; i++)
            {
                if (i % 4_000 == 0 && i > 0) fakeTime.Advance(TimeSpan.FromSeconds(2));
                var now = fakeTime.GetUtcNow();
                var nonce = "nonce_cap_" + i;
                SubgraphSecurityValidator.ValidateSignature("tenant_cap", "user_1", now.ToUnixTimeSeconds().ToString(), nonce, Sign(now, "tenant_cap", nonce), Secret, TimeSpan.FromSeconds(1), "products", "", "POST", "/graphql", "v2", bodySha).ShouldBeTrue();
            }

            SubgraphSecurityValidator.CachedNonceCount.ShouldBeLessThanOrEqualTo(10_000);
            SubgraphSecurityValidator.NonceOrderCount.ShouldBeLessThanOrEqualTo(20_000);
        }
        finally
        {
            SubgraphSecurityValidator.ResetReplayCache();
            SubgraphSecurityValidator.TimeProvider = TimeProvider.System;
        }
    }
}

[Collection("SubgraphReplayCache")]
[Trait("Category", "Security")]
public sealed class SubgraphTimestampRangeTests
{
    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    [InlineData("9999999999999999999")]
    [InlineData("not-a-number")]
    public void OutOfRangeTimestamp_ReturnsFalseWithoutThrowing(string timestamp)
    {
        var result = SubgraphSecurityValidator.ValidateSignature(
            tenant: "t", userSid: "u", timestampStr: timestamp, nonce: "n", signature: "sig",
            signingKey: "key-key-key-key-key-key-key-key-key", subgraph: "products");

        result.ShouldBeFalse();
    }
}

[CollectionDefinition("SubgraphReplayCache", DisableParallelization = true)]
public sealed class SubgraphReplayCacheCollection { }
