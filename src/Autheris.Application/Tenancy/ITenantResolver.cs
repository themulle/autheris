namespace Autheris.Application.Tenancy;

using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Tenancy;

/// <summary>
/// DT-A-2: the only code that decides the tenant of a principal and the only producer of
/// <see cref="TenantResolutionOutcome.DefaultAssigned"/> (DT-SC-01).
/// </summary>
public interface ITenantResolver
{
    /// <summary>The effective default tenant D, or <c>null</c> when none is configured or the configured value is unusable.</summary>
    TenantId? DefaultTenant { get; }

    /// <summary>
    /// D exactly as configured by <c>Gateway:Tenancy:DefaultTenantId</c>, for the start-up audit; <c>null</c> when unset or unusable.
    /// </summary>
    TenantId? ConfiguredDefaultTenant { get; }

    TenantResolution Resolve(ClaimsPrincipal? principal);

    bool IsDefaultTenant(TenantId tenantId);
}
