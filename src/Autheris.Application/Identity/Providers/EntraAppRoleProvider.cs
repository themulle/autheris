namespace Autheris.Application.Identity.Providers;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Identity;

public sealed class EntraAppRoleProvider : IRoleProvider
{
    public const string Id = "entra-app-roles";
    public string ProviderId => Id;

    public Task<ProviderRoleResult> GetRolesAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in identity.RawClaims)
        {
            if (string.Equals(claim.Type, "roles", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    roles.Add(claim.Value.Trim());
                }
            }
        }

        return Task.FromResult(new ProviderRoleResult(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            roles,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }
}
