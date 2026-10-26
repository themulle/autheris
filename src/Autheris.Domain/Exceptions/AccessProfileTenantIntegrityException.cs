namespace Autheris.Domain.Exceptions;

/// <summary>
/// A stored access profile carries a tenant value that is not a valid tenant id (L14, DEF-SC-14). The read is refused (fail closed, 403 via the
/// governed denial mapper) instead of treating the profile as belonging to the caller tenant. The message is fixed, never contains the value.
/// </summary>
public sealed class AccessProfileTenantIntegrityException : GatewaySecurityException
{
    public const string Code = "ACCESS_PROFILE_TENANT_INVALID";

    public AccessProfileTenantIntegrityException()
        : base("The access profile tenant value is invalid; the operation was refused.", Code)
    {
    }
}
