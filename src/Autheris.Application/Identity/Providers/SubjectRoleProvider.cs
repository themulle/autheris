namespace Autheris.Application.Identity.Providers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Identity;

/// <summary>
/// Chain entry of a realm that maps workload subjects to roles (configuration only). It answers with an empty, non-failed result:
/// the mapped roles are applied by the identity pipeline after the per-authentication audit record, so a role cache hit can never skip
/// the audit. Listing this provider in the realm's resolver chain is mandatory for such a realm (startup validation).
/// </summary>
public sealed class SubjectRoleProvider : IRoleProvider
{
    public const string Id = "subject-roles";
    public string ProviderId => Id;

    public Task<ProviderRoleResult> GetRolesAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(realm);
        return Task.FromResult(ProviderRoleResult.Empty);
    }
}
