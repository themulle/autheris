using System.Security.Claims;
using Autheris.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>SEC2-09: /metrics is reachable only for scraper principals, never for any authenticated user.</summary>
[Trait("Category", "Security")]
public sealed class GatewayPoliciesMetricsScrapeTests
{
    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(GatewayPolicies.Configure);
        await using var provider = services.BuildServiceProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        return (await authz.AuthorizeAsync(user, GatewayPolicies.MetricsScrape)).Succeeded;
    }

    private static ClaimsPrincipal Authenticated(params string[] roles)
        => new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim(ClaimTypes.Name, "u")), "Test"));

    [Fact]
    public async Task MetricsScrape_AnonymousCaller_Fails()
        => (await AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()))).ShouldBeFalse();

    [Fact]
    public async Task MetricsScrape_AuthenticatedWithoutRole_Fails()
    {
        (await AuthorizeAsync(Authenticated())).ShouldBeFalse();
        (await AuthorizeAsync(Authenticated("Reader", "GovernanceAdmin"))).ShouldBeFalse();
    }

    [Theory]
    [InlineData("MetricsScraper")]
    [InlineData("ClusterAdmin")]
    public async Task MetricsScrape_ScraperRole_Succeeds(string role)
        => (await AuthorizeAsync(Authenticated(role))).ShouldBeTrue();

    [Fact]
    public void MetricsScrape_PolicyRoles_AreRegisteredForDiagnostics()
    {
        GatewayPolicies.TryGetPolicyRoles(GatewayPolicies.MetricsScrape, out var roles).ShouldBeTrue();
        roles.ShouldBe(["MetricsScraper", "ClusterAdmin"]);
    }
}
