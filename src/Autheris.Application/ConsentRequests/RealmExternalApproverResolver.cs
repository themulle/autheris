namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;

/// <summary>
/// Maps an external approver through the default identity realm's SCIM directory (object SID, user key, external id, user name,
/// UPN). An e-mail address is never an identifier, an ambiguous or inactive user is unmapped, and the resulting principal carries only
/// directory group membership, never roles asserted by the transport.
/// </summary>
public sealed class RealmExternalApproverResolver(
    IScimUserStore? userStore = null,
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null,
    TimeProvider? timeProvider = null) : IExternalApproverResolver
{
    private static readonly TimeSpan GroupResolutionLifetime = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<SecurityPrincipalContext?> ResolveAsync(TenantId tenant, AssertedApprover asserted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asserted);
        if (userStore is null || groupStore is null)
        {
            return null;
        }

        var realmId = realmRegistry?.GetDefaultRealm()?.Id ?? "default";

        var user = await FindUserAsync(tenant, realmId, asserted, ct).ConfigureAwait(false);
        if (user is null || !user.Active || string.IsNullOrWhiteSpace(user.UserKey))
        {
            return null;
        }

        var sidValue = user.ResolvedObjectSid ?? user.UserKey;
        var groups = await groupStore.GetEffectiveGroupSidsForUserAsync(tenant, realmId, user.UserKey, ct: ct).ConfigureAwait(false);
        if (groups.MaxDepthExceeded || groups.HasNonCanonicalSid)
        {
            return null;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sidValue, ClaimValueTypes.String, IdentityClaimIssuers.SynthesizedPrimarySid),
            new(ClaimTypes.NameIdentifier, user.UserKey),
            new("tenant_id", tenant.Value),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete),
            GroupResolutionClaims.CreateExpiresAt(_time.GetUtcNow().Add(GroupResolutionLifetime))
        };
        foreach (var group in groups.GroupObjectSids)
        {
            claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(group));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ExternalWorkflow", ClaimTypes.NameIdentifier, ClaimTypes.Role));
        return SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: tenant, timeProvider: _time);
    }

    private async Task<ScimUser?> FindUserAsync(TenantId tenant, string realmId, AssertedApprover asserted, CancellationToken ct)
    {
        foreach (var candidate in Candidates(asserted))
        {
            var user = await userStore!.GetUserByObjectSidAsync(tenant, realmId, candidate, ct).ConfigureAwait(false)
                ?? await userStore!.GetUserAsync(tenant, realmId, candidate, ct).ConfigureAwait(false)
                ?? await userStore!.GetUserByExternalIdAsync(tenant, realmId, candidate, ct).ConfigureAwait(false)
                ?? await userStore!.GetUserByUserNameAsync(tenant, realmId, candidate, ct).ConfigureAwait(false)
                ?? await userStore!.GetUserByUpnAsync(tenant, realmId, candidate, ct).ConfigureAwait(false);
            if (user is not null)
            {
                return user;
            }
        }

        return null;
    }

    // Only the signed subject is looked up. E-mail and the transport's own SID assertion are display data, not identity.
    private static IEnumerable<string> Candidates(AssertedApprover asserted)
    {
        if (!string.IsNullOrWhiteSpace(asserted.Subject))
        {
            yield return asserted.Subject.Trim();
        }
    }
}
