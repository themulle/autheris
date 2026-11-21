namespace Autheris.Application.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public enum CorrelationStatus
{
    Matched,
    NotProvisioned,
    Deprovisioned,
    Ambiguous
}

public sealed record CorrelationResult(
    CorrelationStatus Status,
    string? ScimUserKey = null,
    string? MatchedByKey = null);

public interface IScimIdentityCorrelator
{
    ValueTask<CorrelationResult> CorrelateAsync(
        ClaimsPrincipal normalized,
        IdentityRealm realm,
        TenantId tenant,
        CancellationToken ct = default);
}

public sealed class ScimIdentityCorrelator(
    IScimUserStore userStore,
    IScimProvisioningStore? provisioningStore = null,
    IAuditWriter? auditWriter = null) : IScimIdentityCorrelator
{
    private static readonly string[] ObjectSidClaimTypes =
    [
        "onprem_sid",
        "objectSid",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid",
        ClaimTypes.PrimarySid,
        "primarysid"
    ];

    private static readonly string[] ImmutableIdClaimTypes =
    [
        "onPremisesImmutableId",
        "onpremisesimmutableid"
    ];

    private static readonly string[] EntraOidClaimTypes =
    [
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier"
    ];

    private static readonly string[] FallbackSubjectClaimTypes =
    [
        "sub",
        ClaimTypes.NameIdentifier
    ];

    private static readonly string[] UpnClaimTypes =
    [
        "upn",
        ClaimTypes.Upn
    ];

    private static readonly string[] FallbackUpnClaimTypes =
    [
        "preferred_username"
    ];

    private readonly IScimUserStore _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
    private readonly IScimProvisioningStore? _provisioningStore = provisioningStore;
    private readonly IAuditWriter? _auditWriter = auditWriter;

    public async ValueTask<CorrelationResult> CorrelateAsync(
        ClaimsPrincipal normalized,
        IdentityRealm realm,
        TenantId tenant,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentNullException.ThrowIfNull(realm);

        var candidateKeys = new List<(int Tier, string KeyName, IReadOnlyList<ScimUser> Matches)>(4);
        var presentKeys = new List<string>(4);

        var objectSids = CollectDistinctValues(normalized, ObjectSidClaimTypes, normalizeLower: false, skipSynthesizedPrimarySid: true);
        if (objectSids.Count > 1 || objectSids.Any(s => !SidFormat.IsCanonical(s)))
        {
            presentKeys.Add("objectSid");
            return await AmbiguousAsync(tenant, realm.Id, "malformed_or_conflicting_objectSid", presentKeys, 0, ct).ConfigureAwait(false);
        }

        if (objectSids.Count == 1)
        {
            presentKeys.Add("objectSid");
            var user = await _userStore.GetUserByObjectSidAsync(tenant, realm.Id, objectSids[0], ct).ConfigureAwait(false);
            candidateKeys.Add((1, "objectSid", user is null ? [] : [user]));
        }

        var immutableIds = CollectDistinctValues(normalized, ImmutableIdClaimTypes, normalizeLower: false);
        if (immutableIds.Count > 1)
        {
            presentKeys.Add("onPremisesImmutableId");
            return await AmbiguousAsync(tenant, realm.Id, "conflicting_onPremisesImmutableId", presentKeys, 0, ct).ConfigureAwait(false);
        }

        if (immutableIds.Count == 1)
        {
            presentKeys.Add("onPremisesImmutableId");
            var user = await _userStore.GetUserByImmutableIdAsync(tenant, realm.Id, immutableIds[0], ct).ConfigureAwait(false);
            candidateKeys.Add((2, "onPremisesImmutableId", user is null ? [] : [user]));
        }

        var oids = CollectDistinctValues(normalized, EntraOidClaimTypes, normalizeLower: true);
        if (oids.Count == 0)
        {
            oids = CollectDistinctValues(normalized, FallbackSubjectClaimTypes, normalizeLower: true);
        }

        if (oids.Count > 1)
        {
            presentKeys.Add("entraObjectId");
            return await AmbiguousAsync(tenant, realm.Id, "conflicting_entraObjectId", presentKeys, 0, ct).ConfigureAwait(false);
        }

        if (oids.Count == 1)
        {
            presentKeys.Add("entraObjectId");
            var matches = await _userStore.GetUsersByEntraObjectIdOrExternalIdAsync(tenant, realm.Id, oids[0], ct).ConfigureAwait(false);
            candidateKeys.Add((3, "entraObjectId", DeduplicateUsers(matches)));
        }

        if (realm.ScimSettings.AllowUpnCorrelation)
        {
            var upns = CollectDistinctValues(normalized, UpnClaimTypes, normalizeLower: true);
            if (upns.Count == 0)
            {
                upns = CollectDistinctValues(normalized, FallbackUpnClaimTypes, normalizeLower: true);
            }

            if (upns.Count > 1)
            {
                presentKeys.Add("userPrincipalName");
                return await AmbiguousAsync(tenant, realm.Id, "conflicting_userPrincipalName", presentKeys, 0, ct).ConfigureAwait(false);
            }

            if (upns.Count == 1)
            {
                presentKeys.Add("userPrincipalName");
                var matches = await _userStore.GetUsersByUpnOrUserNameAsync(tenant, realm.Id, upns[0], ct).ConfigureAwait(false);
                candidateKeys.Add((4, "userPrincipalName", DeduplicateUsers(matches)));
            }
        }

        var allMatchedUsers = new Dictionary<string, ScimUser>(StringComparer.Ordinal);
        (int Tier, string KeyName, ScimUser User)? strongestMatch = null;
        bool anyMultiUserKey = false;

        foreach (var entry in candidateKeys)
        {
            if (entry.Matches.Count > 1)
            {
                anyMultiUserKey = true;
            }

            foreach (var match in entry.Matches)
            {
                if (!string.IsNullOrWhiteSpace(match.Id))
                {
                    allMatchedUsers[match.Id] = match;
                }
            }

            if (entry.Matches.Count == 1 && strongestMatch is null)
            {
                strongestMatch = (entry.Tier, entry.KeyName, entry.Matches[0]);
            }
        }

        if (allMatchedUsers.Count == 0)
        {
            return new CorrelationResult(CorrelationStatus.NotProvisioned);
        }

        if (anyMultiUserKey || allMatchedUsers.Count > 1)
        {
            return await AmbiguousAsync(tenant, realm.Id, "multiple_user_matches", presentKeys, allMatchedUsers.Count, ct).ConfigureAwait(false);
        }

        var chosen = strongestMatch!.Value;
        bool unmatchedStrongerKey = candidateKeys.Any(k => k.Tier < chosen.Tier && k.Matches.Count == 0);
        if (unmatchedStrongerKey)
        {
            return await AmbiguousAsync(tenant, realm.Id, "unmatched_stronger_key", presentKeys, allMatchedUsers.Count, ct).ConfigureAwait(false);
        }

        var userKey = chosen.User.Id!;
        if (!chosen.User.Active || chosen.User.DeactivatedAt.HasValue)
        {
            return new CorrelationResult(CorrelationStatus.Deprovisioned, userKey, chosen.KeyName);
        }

        if (_provisioningStore is not null
            && await _provisioningStore.IsSubjectLockedAsync(tenant, realm.Id, userKey, ct).ConfigureAwait(false))
        {
            return new CorrelationResult(CorrelationStatus.Deprovisioned, userKey, chosen.KeyName);
        }

        return new CorrelationResult(CorrelationStatus.Matched, userKey, chosen.KeyName);
    }

    private async ValueTask<CorrelationResult> AmbiguousAsync(
        TenantId tenant,
        string realmId,
        string reason,
        IReadOnlyList<string> presentKeys,
        int matchedUserCount,
        CancellationToken ct)
    {
        if (_auditWriter is not null)
        {
            var entry = new AuditLogEntry
            {
                TenantId = tenant,
                EventType = AuditEventTypes.IdentityCorrelationAmbiguous,
                ActorSid = new Sid("IDENTITY_PIPELINE"),
                TargetTable = "IDENTITY_USERS",
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    realm_id = realmId,
                    reason,
                    present_keys = presentKeys,
                    matched_user_count = matchedUserCount
                })
            };
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }

        return new CorrelationResult(CorrelationStatus.Ambiguous);
    }

    private static List<string> CollectDistinctValues(ClaimsPrincipal principal, string[] claimTypes, bool normalizeLower, bool skipSynthesizedPrimarySid = false)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(normalizeLower ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var type in claimTypes)
        {
            foreach (var claim in principal.FindAll(type))
            {
                if (skipSynthesizedPrimarySid
                    && string.Equals(claim.Type, ClaimTypes.PrimarySid, StringComparison.Ordinal)
                    && string.Equals(claim.Issuer, IdentityClaimIssuers.SynthesizedPrimarySid, StringComparison.Ordinal))
                {
                    continue;
                }

                var trimmed = claim.Value?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                var normalized = normalizeLower ? trimmed.ToLowerInvariant() : trimmed;
                if (seen.Add(normalized))
                {
                    values.Add(normalized);
                }
            }
        }

        return values;
    }

    private static IReadOnlyList<ScimUser> DeduplicateUsers(IReadOnlyList<ScimUser> users)
    {
        if (users.Count <= 1)
        {
            return users;
        }

        var map = new Dictionary<string, ScimUser>(StringComparer.Ordinal);
        foreach (var u in users)
        {
            var key = u.Id ?? u.ExternalId ?? u.UserName;
            map[key] = u;
        }

        return map.Values.ToList();
    }
}
