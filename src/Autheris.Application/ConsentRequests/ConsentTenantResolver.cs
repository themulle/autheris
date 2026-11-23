namespace Autheris.Application.ConsentRequests;

using System.Security.Claims;
using Autheris.Domain.Tenancy;

/// <summary>
/// Resolves the tenant of a consent request or mutation: the request context tenant (which honours a ClusterAdmin's
/// X-Tenant-ID) wins; the token claim only serves as a cross-check. A different claim tenant is forbidden, except for
/// canonical ClusterAdmins. Without a context tenant the claim tenant applies; without either the tenant is unresolved.
/// </summary>
public static class ConsentTenantResolver
{
    public static TenantId Resolve(ClaimsPrincipal? principal, TenantId contextTenant, bool isCrossTenantAdmin)
    {
        var claim = TenantClaimReader.Read(principal);
        var principalTenant = claim.Kind == ClaimTenantKind.Valid ? (TenantId?)claim.Tenant : null;
        var hasContext = contextTenant.Value is not null;

        if (principalTenant is { } own && hasContext && own != contextTenant && !isCrossTenantAdmin)
        {
            throw new ConsentRequestException("FORBIDDEN", "Cross-tenant access forbidden: The token tenant does not match the tenant of the connection context.");
        }

        if (hasContext)
        {
            return contextTenant;
        }

        return principalTenant ?? throw new Autheris.Domain.Exceptions.TenantUnresolvedException();
    }
}
