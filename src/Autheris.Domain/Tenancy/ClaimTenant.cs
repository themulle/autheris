namespace Autheris.Domain.Tenancy;

using Autheris.Domain.Common;

public enum ClaimTenantKind
{
    /// <summary>The principal carries no tenant statement.</summary>
    Absent = 0,

    /// <summary>One valid, non-reserved tenant id.</summary>
    Valid = 1,

    /// <summary>Unparsable, reserved, blank or conflicting tenant statement; never falls back to the default tenant.</summary>
    Invalid = 2
}

/// <summary>DT-A-2: what a principal says about its tenant (<c>Absent | Valid(TenantId) | Invalid</c>).</summary>
public readonly record struct ClaimTenant
{
    private ClaimTenant(ClaimTenantKind kind, TenantId tenant)
    {
        Kind = kind;
        Tenant = tenant;
    }

    public ClaimTenantKind Kind { get; }

    /// <summary>The tenant when <see cref="Kind"/> is <see cref="ClaimTenantKind.Valid"/>, otherwise <c>default</c>.</summary>
    public TenantId Tenant { get; }

    public static ClaimTenant Absent => new(ClaimTenantKind.Absent, default);

    public static ClaimTenant Invalid => new(ClaimTenantKind.Invalid, default);

    public static ClaimTenant Valid(TenantId tenant) => new(ClaimTenantKind.Valid, tenant);
}
