namespace Autheris.Tests.Unit.Security;

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

public sealed class PerScopeRateLimiterSecurityTests
{
    private static ClientTierResolver CreateResolver(ClientTierOptions? tierOptions = null)
    {
        var gatewayOptions = new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = tierOptions ?? new ClientTierOptions()
            }
        };

        var optionsWrapper = Options.Create(gatewayOptions);
        return new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, optionsWrapper);
    }

    private static ClaimsPrincipal CreatePrincipal(
        string userSid = "user-123",
        string tenantId = "tenant-abc",
        string? clientId = "client-xyz",
        string? scopes = null,
        string? roles = null,
        string? tierClaim = null)
    {
        var claims = new List<Claim>
        {
            new("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", userSid),
            new("http://schemas.microsoft.com/identity/claims/tenantid", tenantId)
        };

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            claims.Add(new("client_id", clientId));
        }

        if (!string.IsNullOrWhiteSpace(scopes))
        {
            claims.Add(new("scp", scopes));
        }

        if (!string.IsNullOrWhiteSpace(roles))
        {
            foreach (var r in roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new(ClaimTypes.Role, r));
            }
        }

        if (!string.IsNullOrWhiteSpace(tierClaim))
        {
            claims.Add(new("tier", tierClaim));
        }

        var identity = new ClaimsIdentity(claims, "Bearer");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV29_01_AntiEscalationTierCeiling()
    {
        // SEC-GOV29-01: A user with Standard tier ceiling cannot elevate to Enterprise
        // simply by presenting an elevated scope (e.g. Admin.Scope).
        var tierOptions = new ClientTierOptions
        {
            RoleTierMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RegularUser"] = "Standard",
                ["AdminUser"] = "Enterprise"
            },
            ScopeTierMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Admin.Elevated"] = "Enterprise",
                ["User.Read"] = "Standard"
            }
        };

        var resolver = CreateResolver(tierOptions);

        // Regular user presents elevated scope Admin.Elevated
        var unprivilegedCaller = CreatePrincipal(
            userSid: "usr-42",
            tenantId: "tenant-1",
            roles: "RegularUser",
            scopes: "Admin.Elevated");

        var context = await resolver.ResolveAsync(unprivilegedCaller, apiKey: null, clientIp: null);

        // Anti-escalation clamping MUST hold: clamped to caller's max permitted role/user tier (Standard)
        context.Tier.ShouldBe(ClientTier.Standard);
        context.SubjectId.ShouldContain("admin.elevated");

        // Legitimate Admin user presenting the same scope elevates up to their Enterprise ceiling
        var adminCaller = CreatePrincipal(
            userSid: "usr-admin",
            tenantId: "tenant-1",
            roles: "AdminUser",
            scopes: "Admin.Elevated");

        var adminContext = await resolver.ResolveAsync(adminCaller, apiKey: null, clientIp: null);
        adminContext.Tier.ShouldBe(ClientTier.Enterprise);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_GOV29_02_ScopeCanonicalizationAndInjectionSanitization()
    {
        // SEC-GOV29-02: Bare scopes must be lowercased, trimmed, deduplicated, and sorted.
        // Delimiters (:, +, {, }, \r, \n) must be stripped/sanitized to prevent Redis / key injection.
        // Array count is bounded to 32, string length to 256.

        // Test 1: Ordering and deduplication
        var principal1 = CreatePrincipal(scopes: "Zeta.Read Alpha.Write Beta.Read Alpha.Write");
        var canonical1 = ClientTierResolver.GetCanonicalScopeIdentifier(principal1);
        canonical1.ShouldBe("alpha.write+beta.read+zeta.read");

        var principal2 = CreatePrincipal(scopes: "beta.read alpha.write zeta.read");
        var canonical2 = ClientTierResolver.GetCanonicalScopeIdentifier(principal2);
        canonical2.ShouldBe("alpha.write+beta.read+zeta.read");
        canonical1.ShouldBe(canonical2);

        // Test 2: Delimiter injection sanitization (: + { } \r \n)
        var maliciousPrincipal = CreatePrincipal(scopes: "evil:key+injection{redis}\r\nhack api://resource/Agent.Read");
        var canonicalMalicious = ClientTierResolver.GetCanonicalScopeIdentifier(maliciousPrincipal);
        canonicalMalicious.ShouldNotBeNull();
        canonicalMalicious.ShouldNotContain(":");
        canonicalMalicious.ShouldNotContain("{");
        canonicalMalicious.ShouldNotContain("}");
        canonicalMalicious.ShouldNotContain("\r");
        canonicalMalicious.ShouldNotContain("\n");
        canonicalMalicious.ShouldContain("agent.read");

        // Test 3: DoS guard: massive scopes list
        var massiveScopes = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            massiveScopes.Add($"Scope_{i:D3}");
        }
        var massivePrincipal = CreatePrincipal(scopes: string.Join(" ", massiveScopes));
        var canonicalMassive = ClientTierResolver.GetCanonicalScopeIdentifier(massivePrincipal);
        canonicalMassive.ShouldNotBeNull();
        canonicalMassive.Length.ShouldBeLessThanOrEqualTo(256);
        canonicalMassive.Split('+').Length.ShouldBeLessThanOrEqualTo(32);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV29_03_UnmappedScopeFallbackFailClosed()
    {
        // SEC-GOV29-03: When token lacks scopes or contains only unmapped scopes,
        // resolver must cleanly fall back to standard subject partitioning and Standard tier without throwing.
        var tierOptions = new ClientTierOptions
        {
            ScopeTierMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Known.Scope"] = "Premium"
            }
        };

        var resolver = CreateResolver(tierOptions);

        // Case A: Token with no scopes at all
        var noScopePrincipal = CreatePrincipal(userSid: "usr-noscope", tenantId: "tenant-x", clientId: "app-1", scopes: null);
        var ctxNoScope = await resolver.ResolveAsync(noScopePrincipal, apiKey: null, clientIp: null);

        ctxNoScope.Tier.ShouldBe(ClientTier.Standard);
        ctxNoScope.SubjectId.ShouldBe("user:tenant-x:usr-noscope:app-1");
        ctxNoScope.ResolvedScope.ShouldBeNull();

        // Case B: Token with unmapped scopes
        var unmappedPrincipal = CreatePrincipal(userSid: "usr-unmapped", tenantId: "tenant-x", clientId: "app-1", scopes: "Unknown.Scope Another.Scope");
        var ctxUnmapped = await resolver.ResolveAsync(unmappedPrincipal, apiKey: null, clientIp: null);

        ctxUnmapped.Tier.ShouldBe(ClientTier.Standard);
        ctxUnmapped.SubjectId.ShouldBe("user:tenant-x:usr-unmapped:app-1:another.scope+unknown.scope");
        ctxUnmapped.ResolvedScope.ShouldBe("another.scope+unknown.scope");

        // Case C: Unrecognized tier string fails closed to Standard
        var invalidTierOptions = new ClientTierOptions
        {
            ScopeTierMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bad.Scope"] = "SUPER_ADMIN_ULTRA"
            }
        };
        var invalidTierResolver = CreateResolver(invalidTierOptions);
        var badScopePrincipal = CreatePrincipal(scopes: "Bad.Scope");
        var ctxBadScope = await invalidTierResolver.ResolveAsync(badScopePrincipal, apiKey: null, clientIp: null);
        ctxBadScope.Tier.ShouldBe(ClientTier.Standard);
    }

    [Fact]
    public async Task ScopeLimits_OverridesSpecificLimitsForScope()
    {
        var tierOptions = new ClientTierOptions
        {
            ScopeLimits = new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["Agent.Read"] = new ClientTierLimitOverride
                {
                    MaxTokensCapacity = 300,
                    TokenRefillRatePerSecond = 5.0
                }
            }
        };

        var resolver = CreateResolver(tierOptions);
        var agentPrincipal = CreatePrincipal(scopes: "Agent.Read");

        var context = await resolver.ResolveAsync(agentPrincipal, apiKey: null, clientIp: null);
        context.Policy.MaxTokensCapacity.ShouldBe(300);
        context.Policy.TokenRefillRatePerSecond.ShouldBe(5.0);
    }
}
