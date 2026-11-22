using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Api;

[Trait("Category", "Security")]
public sealed class GatewayPoliciesMcpCallerTests
{
    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(GatewayPolicies.Configure);
        await using var provider = services.BuildServiceProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        return (await authz.AuthorizeAsync(user, GatewayPolicies.McpCaller)).Succeeded;
    }

    private static ClaimsPrincipal Authenticated(params string[] roles)
        => new(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim(ClaimTypes.Name, "mcp-user")),
            "Test"));

    [Fact]
    public async Task McpCaller_AnonymousCaller_Fails()
        => (await AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()))).ShouldBeFalse();

    [Fact]
    public async Task McpCaller_AuthenticatedWithoutRequiredRole_FailsClosed()
    {
        (await AuthorizeAsync(Authenticated())).ShouldBeFalse();
        (await AuthorizeAsync(Authenticated("MetricsScraper", "SecurityAuditor", "SCIM_PROVISIONER"))).ShouldBeFalse();
    }

    [Theory]
    [InlineData("AiAgent")]
    [InlineData("McpCaller")]
    [InlineData("Developer")]
    [InlineData("Consumer")]
    [InlineData("DataSteward")]
    [InlineData("DataOwner")]
    [InlineData("GovernanceAdmin")]
    [InlineData("TenantAdmin")]
    [InlineData("ClusterAdmin")]
    public async Task McpCaller_AllowedRole_Succeeds(string role)
        => (await AuthorizeAsync(Authenticated(role))).ShouldBeTrue();

    [Fact]
    public void McpCaller_TryGetPolicyRoles_MatchesPolicyRoles()
    {
        GatewayPolicies.TryGetPolicyRoles(GatewayPolicies.McpCaller, out var roles).ShouldBeTrue();
        roles.ShouldBe(GatewayPolicies.McpCallerRoles);
    }
}
