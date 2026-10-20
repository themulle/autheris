namespace Autheris.Application.Identity;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Identity;

public interface IRoleProvider
{
    string ProviderId { get; }
    string Name => ProviderId;
    Task<ProviderRoleResult> GetRolesAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default);
}

public interface IRoleResolver
{
    Task<RoleResolution> ResolveAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default);

    Task<RoleResolution> ResolveRolesAsync(
        ResolvedIdentity identity,
        CancellationToken cancellationToken = default);
}
