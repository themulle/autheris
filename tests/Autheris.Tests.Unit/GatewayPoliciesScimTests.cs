using System.Security.Claims;
using Autheris.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// Security review: the SCIM endpoints referenced a scheme name as policy name (no such policy) and the SCIM secret
/// granted ClusterAdmin. The "Scim" policy accepts only an identity of the SCIM scheme with the provisioner role.
/// </summary>
[Trait("Category", "Security")]
public sealed class GatewayPoliciesScimTests
{
    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(GatewayPolicies.Configure);
        await using var provider = services.BuildServiceProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        return (await authz.AuthorizeAsync(user, GatewayPolicies.Scim)).Succeeded;
    }

    private static ClaimsPrincipal Principal(string authenticationType, params string[] roles)
        => new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim(ClaimTypes.Name, "u")), authenticationType));

    [Fact]
    public void Scim_PolicyIsRegistered_AndBoundToTheScimScheme()
    {
        var options = new AuthorizationOptions();
        GatewayPolicies.Configure(options);

        var policy = options.GetPolicy(GatewayPolicies.Scim);
        policy.ShouldNotBeNull();
        policy!.AuthenticationSchemes.Count.ShouldBe(1);
        policy.AuthenticationSchemes.ShouldContain(GatewayAuthSchemes.Scim);
        GatewayPolicies.TryGetPolicyRoles(GatewayPolicies.Scim, out var roles).ShouldBeTrue();
        roles.ShouldBe([GatewayPolicies.ScimProvisionerRole]);
    }

    [Fact]
    public async Task Scim_ScimSchemeProvisioner_Succeeds()
        => (await AuthorizeAsync(Principal(GatewayAuthSchemes.Scim, GatewayPolicies.ScimProvisionerRole))).ShouldBeTrue();

    [Fact]
    public async Task Scim_ClusterAdminFromOtherScheme_Fails()
        => (await AuthorizeAsync(Principal("Bearer", "ClusterAdmin"))).ShouldBeFalse();

    [Fact]
    public async Task Scim_ProvisionerRoleFromOtherScheme_Fails()
        => (await AuthorizeAsync(Principal("Bearer", GatewayPolicies.ScimProvisionerRole))).ShouldBeFalse();

    [Fact]
    public async Task Scim_ScimSchemeWithoutProvisionerRole_Fails()
        => (await AuthorizeAsync(Principal(GatewayAuthSchemes.Scim))).ShouldBeFalse();

    [Fact]
    public async Task Scim_Anonymous_Fails()
        => (await AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()))).ShouldBeFalse();
}
