using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Autheris.Tests.Parity.Infrastructure;

/// <summary>A running parity host (SQLite in-process or a container dialect); channels only need an authenticated client.</summary>
public interface IParityHost
{
    HttpClient CreateClient();
}

/// <summary>Settings shared by every parity host (SQLite and container dialects). Test auth is enabled only here (SEC-PAR-03).</summary>
public static class ParityHostSettings
{
    public static void Apply(IWebHostBuilder b, string governancePath)
    {
        b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
        b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "100000");
        // The sweeps send hundreds of GraphQL queries from one subject; lift the per-subject cost budget so it cannot mask results.
        b.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:MaxCostPerMinute", "1000000");
        b.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokenBucketCapacity", "100000");
        b.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokensPerSecond", "10000");
        b.UseSetting("Gateway:GraphQL:MaxAllowedComplexity", "5000");
        b.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxCostPerQuery", "5000");
        b.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxTokensCapacity", "100000000");
        b.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:TokenRefillRatePerSecond", "1000000");
        // k-anonymity: governed aggregations hide groups below the minimum group size (default 5). Lifted here so that exact group
        // results of the 18-row seed can be asserted; the suppression itself is out of scope of the parity suite.
        // MISC-10: a floor of 1 is only valid with the explicit AllowDisabled opt-in (production validation stays untouched).
        b.UseSetting("Analytics:Privacy:AllowDisabled", "true");
        b.UseSetting("Analytics:Privacy:DefaultMinGroupSize", "1");
        b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
        b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source={governancePath}");
        b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
        b.UseSetting("Gateway:Rebac:Enabled", "false");
        b.UseSetting("Gateway:OpenSchema", "true");
        b.UseSetting("Gateway:Tenancy:Mode", "Multi");
    }

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory, string roles = "Reader", string tenant = "tenant_parity")
    {
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-PARITY");
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        return client;
    }
}
