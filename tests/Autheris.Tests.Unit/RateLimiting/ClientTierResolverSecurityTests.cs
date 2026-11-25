using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Unit.RateLimiting;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.State.Caching.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ClientTierResolverSecurityTests
{
    private readonly ClientTierResolver _resolver = new(NullLogger<ClientTierResolver>.Instance);

    [Theory]
    [InlineData("enterprise")]
    [InlineData("my-enterprise-key")]
    [InlineData("key_enterprise_123")]
    [InlineData("ATTACKER_ENTERPRISE_TOKEN")]
    public async Task ResolveAsync_WithEnterpriseSubstring_DoesNotGrantEnterpriseTier(string maliciousApiKey)
    {
        var context = await _resolver.ResolveAsync(null, maliciousApiKey, "127.0.0.1");

        // SEC-1 / SEC-06: Must not grant Enterprise or Standard tier just because the key contains "enterprise"
        context.Tier.ShouldNotBe(ClientTier.Enterprise);
        context.Tier.ShouldBe(ClientTier.Free);
        context.SubjectId.ShouldNotBe("api_enterprise");
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("company_internal_secret")]
    public async Task ResolveAsync_WithInternalSubstring_DoesNotGrantInternalTier(string maliciousApiKey)
    {
        var context = await _resolver.ResolveAsync(null, maliciousApiKey, "127.0.0.1");

        context.Tier.ShouldNotBe(ClientTier.Internal);
        context.Tier.ShouldBe(ClientTier.Free);
        context.SubjectId.ShouldNotBe("api_internal");
    }

    [Fact]
    public async Task ResolveAsync_RegisteredApiKey_GrantsAssignedTier()
    {
        _resolver.RegisterApiKey("valid-prod-key-123", ClientTier.Standard);
        _resolver.RegisterApiKey("enterprise-vip-client", ClientTier.Enterprise);

        var context1 = await _resolver.ResolveAsync(null, "valid-prod-key-123", "127.0.0.1");
        context1.Tier.ShouldBe(ClientTier.Standard);

        var context2 = await _resolver.ResolveAsync(null, "enterprise-vip-client", "127.0.0.1");
        context2.Tier.ShouldBe(ClientTier.Enterprise);

        var unknownContext = await _resolver.ResolveAsync(null, "random-unregistered-key", "127.0.0.1");
        unknownContext.Tier.ShouldBe(ClientTier.Free);
    }

    [Fact]
    public async Task ResolveAsync_DifferentUnregisteredApiKeys_FallBackToSameIpBucket()
    {
        // SEC M-16: Unbekannte API-Keys werden ignoriert; frueher erzeugte jeder zufaellige Key einen eigenen Bucket.
        var context1 = await _resolver.ResolveAsync(null, "key-alpha", "127.0.0.1");
        var context2 = await _resolver.ResolveAsync(null, "key-beta", "127.0.0.1");

        context1.SubjectId.ShouldBe(context2.SubjectId);
        context1.SubjectId.ShouldBe("anon_127.0.0.1");
    }

    [Fact]
    public async Task ResolveAsync_Anonymous_ReturnsFreeTierWithIpSubject()
    {
        var context = await _resolver.ResolveAsync(null, null, "192.168.1.50");

        context.Tier.ShouldBe(ClientTier.Free);
        context.SubjectId.ShouldBe("anon_192.168.1.50");
    }

    [Fact]
    public async Task ResolveAsync_AuthenticatedUserWithExplicitTierClaim_ReturnsClaimTier()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-42"),
            new Claim("tier", "Enterprise")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims.Append(DefaultTenantCaller.Claim), "Bearer"));

        var context = await _resolver.ResolveAsync(principal, null, "127.0.0.1");

        context.Tier.ShouldBe(ClientTier.Enterprise);
        // SEC M-16: Subjekt = Tenant + Benutzer-SID
        context.SubjectId.ShouldBe($"user:{Autheris.Tests.Common.TestTenants.Default.Value}:user-42");
    }

    [Fact]
    public async Task ResolveAsync_ScopeTierMappingAboveStandardWithoutUserCeiling_DoesNotClampToStandard()
    {
        var options = Options.Create(new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    ScopeTierMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["analytics:enterprise"] = "Enterprise"
                    }
                }
            }
        });
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, options);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "user-scope"),
            new Claim("scp", "analytics:enterprise"),
            DefaultTenantCaller.Claim
        ], "Bearer"));

        var context = await resolver.ResolveAsync(principal, null, null);

        context.Tier.ShouldBe(ClientTier.Enterprise);
        context.ResolvedScope.ShouldBe("analyticsenterprise");
    }

    [Fact]
    public async Task ResolveAsync_ExtraUnmappedOidcScopes_KeyByMatchedConfiguredScopeOrDefault()
    {
        var options = Options.Create(new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    ScopeTierMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mcp:query"] = "Standard"
                    },
                    ScopeLimits = new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mcp:query"] = new ClientTierLimitOverride { MaxConcurrentQueries = 1 }
                    }
                }
            }
        });
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, options);

        ClaimsPrincipal Principal(string scopes) => new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "user-42"),
            new Claim("scp", scopes),
            DefaultTenantCaller.Claim
        ], "Bearer"));

        var configuredOnly = await resolver.ResolveAsync(Principal("mcp:query"), null, null);
        var withOidcNoise = await resolver.ResolveAsync(Principal("mcp:query openid profile email offline_access"), null, null);
        var unmappedA = await resolver.ResolveAsync(Principal("openid"), null, null);
        var unmappedB = await resolver.ResolveAsync(Principal("openid profile email"), null, null);

        configuredOnly.ResolvedScope.ShouldBe("mcpquery");
        withOidcNoise.SubjectId.ShouldBe(configuredOnly.SubjectId);
        withOidcNoise.ResolvedScope.ShouldBe(configuredOnly.ResolvedScope);

        unmappedA.ResolvedScope.ShouldBe("default");
        unmappedB.SubjectId.ShouldBe(unmappedA.SubjectId);
        unmappedB.ResolvedScope.ShouldBe("default");
    }
}
