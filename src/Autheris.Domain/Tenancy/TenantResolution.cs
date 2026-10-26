namespace Autheris.Domain.Tenancy;

using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;

public enum TenantResolutionOutcome
{
    /// <summary>No authenticated principal; no tenant is decided.</summary>
    Unauthenticated = 0,

    /// <summary>Tenant taken from the principal's tenant claim.</summary>
    FromClaim = 1,

    /// <summary>Tenant bound by the principal's identity realm.</summary>
    FromRealm = 2,

    /// <summary>No tenant statement; the configured default tenant was assigned. Produced only by the tenant resolver (DT-SC-01).</summary>
    DefaultAssigned = 3,

    /// <summary>No tenant statement and no default tenant configured.</summary>
    Unresolved = 4,

    /// <summary>Invalid, reserved or conflicting tenant statement (or an unusable default); never falls back to the default.</summary>
    Invalid = 5
}

/// <summary>DT-A-2: the result of resolving a principal's tenant.</summary>
public readonly record struct TenantResolution(TenantResolutionOutcome Outcome, TenantId? Tenant)
{
    /// <summary>The tenant, or a fixed-message <see cref="GatewaySecurityException"/> without any claim value (DT-SC-02).</summary>
    public TenantId RequireTenant() => Outcome switch
    {
        TenantResolutionOutcome.FromClaim or TenantResolutionOutcome.FromRealm or TenantResolutionOutcome.DefaultAssigned
            when Tenant is { } tenant && tenant.Value is not null => tenant,
        TenantResolutionOutcome.Invalid => throw new TenantClaimInvalidException(),
        _ => throw new TenantUnresolvedException()
    };
}
