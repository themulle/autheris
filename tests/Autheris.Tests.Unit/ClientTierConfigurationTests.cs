namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Caching.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class ClientTierConfigurationTests
{
    [Fact]
    public async Task ResolveAsync_WithConfiguredApiKey_ResolvesConfiguredTier()
    {
        var options = new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    ApiKeys = new Dictionary<string, string>
                    {
                        ["my-enterprise-key"] = "Enterprise"
                    }
                }
            }
        };

        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options));
        var context = await resolver.ResolveAsync(principal: null, apiKey: "my-enterprise-key", clientIp: "127.0.0.1");

        context.Tier.ShouldBe(ClientTier.Enterprise);
        context.Policy.Tier.ShouldBe(ClientTier.Enterprise);
    }

    [Fact]
    public async Task ResolveAsync_WithRoleTierMapping_ResolvesMappedRoleTier()
    {
        var options = new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    RoleTierMappings = new Dictionary<string, string>
                    {
                        ["PartnerAdmin"] = "Enterprise",
                        ["Consumer"] = "Standard"
                    }
                }
            }
        };

        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options));
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
                new Claim(ClaimTypes.Role, "PartnerAdmin"),
                new Claim("tenant_id", "tenant-1")
            ],
            "Test"));

        var context = await resolver.ResolveAsync(principal: user, apiKey: null, clientIp: null);

        context.Tier.ShouldBe(ClientTier.Enterprise);
        context.Policy.Tier.ShouldBe(ClientTier.Enterprise);
    }

    [Fact]
    public async Task ResolveAsync_WithTierLimitOverride_AppliesConfiguredMaxCostPerQuery()
    {
        var options = new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    TierLimits = new Dictionary<string, ClientTierLimitOverride>
                    {
                        ["Standard"] = new ClientTierLimitOverride
                        {
                            MaxCostPerQuery = 750
                        }
                    }
                }
            }
        };

        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options));
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
                new Claim("tenant_id", "tenant-1")
            ],
            "Test"));

        var context = await resolver.ResolveAsync(principal: user, apiKey: null, clientIp: null);

        context.Tier.ShouldBe(ClientTier.Standard);
        context.Policy.MaxCostPerQuery.ShouldBe(750);
        context.Policy.MaxComplexityDepth.ShouldBe(10); // Unchanged default
    }
}
