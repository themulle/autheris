namespace Autheris.Domain.Options;

/// <summary>
/// <c>Gateway:Tenancy</c>. Single- versus multi-tenant is a deployment setting (a configured default tenant) and is never inferred from a missing tenant claim.
/// </summary>
public sealed class TenancyOptions
{
    public const string SectionName = "Gateway:Tenancy";

    /// <summary>
    /// <c>Gateway:Tenancy:DefaultTenantId</c> (DT-A-1): the tenant of principals without a tenant claim. Absent or null means no default;
    /// an empty or unusable value fails start-up. Read only by the tenant resolver and option validation (DT-SC-01).
    /// </summary>
    public string? DefaultTenantId { get; init; }
}
