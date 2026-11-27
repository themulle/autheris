namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Quota;
using Autheris.Application.Mcp.Services;
using Autheris.Application.State.Caching.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>The effective query limits of the calling token: correct values with their source, and nothing of another caller.</summary>
[Trait("Category", "Security")]
public sealed class CallerQueryLimitsServiceTests
{
    private sealed class FixedMonitor(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private static GatewayOptions Options() => new()
    {
        WebSql = new WebSqlOptions { Enabled = true },
        
        RateLimiting = new RateLimitingOptions
        {
            ClientTiers = new ClientTierOptions
            {
                ScopeLimits = new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["agent.read"] = new() { MaxResultRows = 50, MaxConcurrentQueries = 2, MaxQueryDurationSeconds = 7, MaxCostPerQuery = 33 },
                    ["analyst.wide"] = new() { MaxResultRows = 7777, MaxConcurrentQueries = 9, MaxQueryDurationSeconds = 123 }
                }
            }
        }
    };

    private static ClaimsPrincipal Caller(string sid, string? scope)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, sid), new("sid", sid), DefaultTenantCaller.Claim };
        if (scope is not null)
        {
            claims.Add(new Claim("scp", scope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static CallerQueryLimitsService Service(GatewayOptions o) =>
        new(new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Microsoft.Extensions.Options.Options.Create(o)), new FixedMonitor(o));

    [Fact]
    public async Task ScopeLimits_DifferPerToken_AndCarryTheirSource()
    {
        var service = Service(Options());

        var agent = await service.ResolveAsync(Caller("u1", "agent.read"), null, null);
        var plain = await service.ResolveAsync(Caller("u2", null), null, null);

        agent.MaxResultRows.ShouldBe(new LimitValue(50, "scope"));
        agent.Concurrency.QueriesAtOnce.ShouldBe(new LimitValue(2, "scope"));
        agent.MaxQueryDurationSeconds.ShouldBe(new LimitValue(7, "scope"));
        agent.Sql!.DefaultRows.ShouldBe(new LimitValue(50, "scope"));
        agent.Sql.MaxRowsWithLimit.ShouldBe(new LimitValue(50, "scope"));
        agent.Sql.ExecutionTimeoutSeconds.ShouldBe(new LimitValue(7, "scope"));
        agent.GraphQl.MaxCostPerQuery.ShouldBe(new LimitValue(33, "scope"));

        plain.MaxResultRows.Unlimited.ShouldBeTrue();
        plain.Sql!.DefaultRows.ShouldBe(new LimitValue(1000, "installation"));
        plain.Sql.MaxRowsWithLimit.ShouldBe(new LimitValue(10000, "installation"));
        plain.Sql.MaxOffset.ShouldBe(new LimitValue(10000, "installation"));
        plain.Sql.MaxListValues.ShouldBe(new LimitValue(1000, "installation"));
        plain.Sql.ExecutionTimeoutSeconds.ShouldBe(new LimitValue(30, "installation"));
        plain.Concurrency.ReadsPerTable.ShouldBe(new LimitValue(4, "installation"));
        plain.Concurrency.QueriesAtOnce.Unlimited.ShouldBeTrue();
    }

    [Fact]
    public async Task Payload_NeverContainsAnotherScopesValuesOrNames()
    {
        var service = Service(Options());

        var agent = await service.ResolveAsync(Caller("u1", "agent.read"), null, null);
        var json = JsonSerializer.Serialize(agent, McpResultJson.Options);

        json.ShouldNotContain("7777");
        json.ShouldNotContain("123,");
        json.ShouldNotContain("analyst");
        json.ShouldNotContain("agent");
        json.ShouldNotContain("u1");
        json.ShouldNotContain("tenant", Case.Insensitive);
    }

    [Fact]
    public async Task DisabledWebSql_IsOmitted()
    {
        var service = Service(new GatewayOptions());

        var limits = await service.ResolveAsync(Caller("u1", null), null, null);

        limits.Sql.ShouldBeNull();
    }

    [Fact]
    public async Task UnauthenticatedCaller_IsRejected()
    {
        var service = Service(Options());

        await Should.ThrowAsync<UnauthorizedAccessException>(() => service.ResolveAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, "10.0.0.1"));
    }

    [Fact]
    public async Task GuideSection_ShowsTheCallersValues()
    {
        var service = Service(Options());
        var limits = await service.ResolveAsync(Caller("u1", "agent.read"), null, null);

        var section = McpCallerLimitsGuideSection.Render(limits);

        section.ShouldContain("## Your limits");
        section.ShouldContain("**Result rows of the scope:** 50 (source: scope)");
        section.ShouldContain("**Concurrent reads per table:** 4 (source: installation)");
        section.ShouldContain("Retry-After");
        section.ShouldNotContain("7777");
    }
}
