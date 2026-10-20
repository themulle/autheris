namespace Autheris.Application.Identity.Providers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;

public sealed class ScimStoreRoleProvider : IRoleProvider
{
    public const string Id = "scim-store";
    public string ProviderId => Id;

    private readonly IIdentityStoreRepository _repository;

    public ScimStoreRoleProvider(IIdentityStoreRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<ProviderRoleResult> GetRolesAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(realm);

        try
        {
            var groups = await _repository.GetGroupMembershipsForUserAsync(
                identity.TenantId, realm.Id, identity.UserKey, cancellationToken).ConfigureAwait(false);

            var groupSet = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
            return new ProviderRoleResult(
                groupSet,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // SEC-SC-12: Fail closed on store failure
            return ProviderRoleResult.Empty;
        }
    }
}
