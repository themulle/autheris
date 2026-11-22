namespace Autheris.Application.Common.Interfaces;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Authoritative capability boundary separating governance authority, IT administration,
/// and container ownership scope.
/// </summary>
public interface IGovernanceCapabilities
{
    /// <summary>True when the principal holds tenant-valid governance admin privileges for <paramref name="tenant"/>.</summary>
    bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant);

    /// <summary>True when the principal holds canonical ClusterAdmin or tenant-valid IT administration for <paramref name="tenant"/>.</summary>
    bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant);

    /// <summary>True when the principal belongs to the owner group of <paramref name="container"/>.</summary>
    ValueTask<bool> IsOwnerMemberAsync(
        SecurityPrincipalContext principal,
        ContainerRef container,
        CancellationToken ct);

    /// <summary>True when the principal may classify assets in <paramref name="container"/> within <paramref name="objectTenant"/>.</summary>
    ValueTask<bool> CanClassifyAsync(
        SecurityPrincipalContext principal,
        ContainerRef container,
        TenantId objectTenant,
        CancellationToken ct);

    /// <summary>True when the principal may edit governance policies within <paramref name="objectTenant"/>.</summary>
    bool CanEditPolicy(ClaimsPrincipal? principal, TenantId objectTenant);
}
