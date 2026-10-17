namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

/// <summary>
/// Builds the <see cref="CallerSubject"/> of a principal (PLAN-GOV-129 A-6, BC-129-01, BC-129-02). It deliberately does not use
/// <c>GetUserSid()</c> (which falls back to <c>appid</c>/<c>client_id</c>/<c>azp</c>) and not the claim-presence classification of the
/// identity resolver: a caller is a <see cref="CallerSubjectKind.User"/> only with a non-app token, an immutable person key and an
/// identity of an allowed authentication scheme. UPN, mail and display names are never keys. Anything unclassifiable is
/// <see cref="CallerSubjectKind.None"/> (every attribute <c>Missing</c>).
/// </summary>
public static class CallerSubjectFactory
{
    private static readonly string[] ObjectIdTypes = ["oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"];
    private static readonly string[] SidTypes =
    [
        ClaimTypes.PrimarySid, "objectSid", "onprem_sid", "primarysid", "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid"
    ];

    private static readonly string[] SubjectTypes = ["sub", ClaimTypes.NameIdentifier];
    private static readonly string[] ApplicationIdTypes = ["appid", "azp", "client_id"];
    private static readonly string[] HumanTypes = [ClaimTypes.Upn, "upn", ClaimTypes.Email, "email", "preferred_username", "unique_name"];

    public static CallerSubject Create(ClaimsPrincipal? principal, TenantId tenant, CallerAttributeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (principal is null)
        {
            return CallerSubject.NoSubject(tenant);
        }

        var identity = principal.Identities.FirstOrDefault(i => CallerAttributeSchemeGate.Passes(i, options));
        if (identity is null)
        {
            return CallerSubject.NoSubject(tenant);
        }

        var claims = identity.Claims.ToArray();
        var personIssuers = options.Claims.TrustedIssuers.Where(t => t.SubjectKeyIsPerson).Select(t => t.Issuer).ToHashSet(StringComparer.Ordinal);

        string? First(string[] types, out Claim? claim)
        {
            claim = claims.FirstOrDefault(c => types.Contains(c.Type, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(c.Value));
            return claim?.Value.Trim();
        }

        var idType = claims.FirstOrDefault(c => c.Type == "idtyp")?.Value;
        var appIds = claims
            .Where(c => ApplicationIdTypes.Contains(c.Type, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => CallerAttributeNameRules.CanonicalApplicationId(c.Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var human = claims.Any(c => HumanTypes.Contains(c.Type, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(c.Value));

        string? oidRaw = First(ObjectIdTypes, out var oidClaim);
        string? sid = First(SidTypes, out var sidClaim);
        string? subRaw = First(SubjectTypes, out var subClaim);
        var personKeys = new[] { oidRaw, sid, subRaw }.Where(k => !string.IsNullOrEmpty(k)).Select(k => CallerAttributeNameRules.CanonicalApplicationId(k!)).ToList();

        bool isApp = string.Equals(idType, "app", StringComparison.OrdinalIgnoreCase)
            || appIds.Any(a => personKeys.Contains(a, StringComparer.OrdinalIgnoreCase))
            || (oidRaw is not null && subRaw is not null && !human && string.Equals(oidRaw, subRaw, StringComparison.OrdinalIgnoreCase));
        if (isApp)
        {
            return new CallerSubject(tenant, CallerSubjectKind.Application, appIds.Count == 1 ? appIds[0] : null, claims, oidClaim?.Issuer ?? subClaim?.Issuer, identity.AuthenticationType);
        }

        var keys = new Dictionary<SubjectKeyKind, string>();
        string? issuer = null;
        if (oidRaw is not null && Guid.TryParse(oidRaw, out var oid))
        {
            keys[SubjectKeyKind.EntraObjectId] = oid.ToString("D", CultureInfo.InvariantCulture);
            issuer ??= oidClaim!.Issuer;
        }

        if (sid is not null && sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            keys[SubjectKeyKind.WindowsSid] = sid;
            issuer ??= sidClaim!.Issuer;
        }

        if (subRaw is not null && subClaim is not null && personIssuers.Contains(subClaim.Issuer))
        {
            keys[SubjectKeyKind.Subject] = subRaw;
            issuer ??= subClaim.Issuer;
        }

        return keys.Count == 0
            ? CallerSubject.NoSubject(tenant)
            : new CallerSubject(tenant, CallerSubjectKind.User, null, claims, issuer, identity.AuthenticationType, keys.ToFrozenDictionary());
    }
}
