using Autheris.Domain.Exceptions;

namespace Autheris.Domain.Common;

/// <summary>
/// DT-A-9 / DT-SC-04 / DT-SC-16: the only way persistence turns a stored tenant value into a <see cref="TenantId"/> and the guard every
/// writer applies before it binds a tenant. There is no fallback: a NULL, empty or invalid stored value is an integrity error, and a
/// <c>default(TenantId)</c> is never written.
/// </summary>
public static class StoredTenant
{
    /// <summary>Parses a stored request-tenant value; NULL, empty, invalid or reserved values throw <see cref="StoredTenantIntegrityException"/>.</summary>
    public static TenantId Parse(string? raw)
    {
        if (TenantId.TryParse(raw, out var tenant))
        {
            return tenant;
        }

        throw new StoredTenantIntegrityException();
    }

    /// <summary>
    /// Parses a stored audit tenant: like <see cref="Parse"/>, plus the platform bucket <see cref="TenantId.Platform"/> that audit rows of
    /// tenant-less platform records carry (DT-A-6).
    /// </summary>
    public static TenantId ParseAudit(string? raw)
    {
        if (string.Equals(raw, TenantId.Platform.Value, StringComparison.Ordinal))
        {
            return TenantId.Platform;
        }

        return Parse(raw);
    }

    /// <summary>True when <see cref="ParseAudit"/> would accept the stored value (for verification paths that report instead of throwing).</summary>
    public static bool IsValidAudit(string? raw) =>
        string.Equals(raw, TenantId.Platform.Value, StringComparison.Ordinal) || TenantId.TryParse(raw, out _);

    /// <summary>Returns the value a writer binds; <c>default(TenantId)</c> (null or whitespace value) throws <see cref="ArgumentException"/>.</summary>
    public static string RequireWritable(TenantId tenant, string paramName = "tenant")
    {
        if (string.IsNullOrWhiteSpace(tenant.Value))
        {
            throw new ArgumentException("A tenant is required; default(TenantId) is not a tenant.", paramName);
        }

        return tenant.Value;
    }
}
