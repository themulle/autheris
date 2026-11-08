namespace Autheris.Tests.Unit.Governance;

using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-SIMPL-87 (OD-9): global operations are reserved to the canonical ClusterAdmin; GovernanceAdmin is always tenant-bound,
/// and a token without a tenant claim resolves to the default tenant (never to a global scope).
/// </summary>
[Trait("Category", "Security")]
public sealed class Batch5OD9ScopeTests
{
    private static ClaimsPrincipal Principal(string? tenant, string role, bool defaultStamped = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "admin"), new(ClaimTypes.Role, role) };
        if (tenant is not null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        if (defaultStamped)
        {
            claims.Add(new Claim("autheris:tenant_status", "default"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public void GovernanceAdmin_without_tenant_claim_is_not_global()
    {
        var principal = Principal(null, "GovernanceAdmin");

        EndpointSecurity.IsGlobalGovernanceAdmin(principal).ShouldBeFalse();
        EndpointSecurity.IsTenantBoundGovernanceAdmin(principal).ShouldBeTrue();
    }

    [Fact]
    public void GovernanceAdmin_with_tenant_is_tenant_bound()
    {
        var principal = Principal("tenant-a", "GovernanceAdmin");

        EndpointSecurity.IsGlobalGovernanceAdmin(principal).ShouldBeFalse();
        EndpointSecurity.IsTenantBoundGovernanceAdmin(principal).ShouldBeTrue();
    }

    [Fact]
    public void Canonical_ClusterAdmin_is_global()
    {
        var principal = Principal("tenant-a", "ClusterAdmin");

        EndpointSecurity.IsGlobalGovernanceAdmin(principal).ShouldBeTrue();
        EndpointSecurity.IsTenantBoundGovernanceAdmin(principal).ShouldBeFalse();
        EndpointSecurity.IsGlobalGovernanceAdmin(null).ShouldBeFalse();
    }

    [Fact]
    public void Token_without_tenant_claim_uses_default_tenant()
    {
        // The claims transformation stamps the configured default tenant (status "default") on a claim-less token.
        var principal = Principal("dflt", "GovernanceAdmin", defaultStamped: true);

        principal.GetTenantId().Value.ShouldBe("dflt");
        EndpointSecurity.IsGlobalGovernanceAdmin(principal).ShouldBeFalse();
        EndpointSecurity.IsTenantBoundGovernanceAdmin(principal).ShouldBeTrue();
    }
}
