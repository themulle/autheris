namespace Autheris.Application.Governance.Ownership;

using System;
using System.Collections.Generic;
using System.Text;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Security;

/// <summary>
/// Normalized lookup keys of deputy relation identities (issue #1126). A stored identity is realm-qualified (<c>realm|userKey</c> or
/// <c>realm|objectSid</c>); a caller is matched under every identifier form the separation-of-duties rules know, each also qualified with
/// the realm the authentication pipeline bound the caller to. A bare identifier is never matched against a stored relation, so a token of
/// another realm carrying the same bare value never acts as a deputy.
/// </summary>
public static class DeputyKey
{
    public const int MaxLength = 256;

    /// <summary>The key of <paramref name="identifier"/> (trimmed, NFKC, upper-case); empty when blank.</summary>
    public static string For(string? identifier) =>
        string.IsNullOrWhiteSpace(identifier) ? string.Empty : identifier.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    /// <summary>The key of the realm-qualified form <c>realm|value</c>; empty when either part is blank.</summary>
    public static string Qualified(string? realmId, string? value) =>
        string.IsNullOrWhiteSpace(realmId) || string.IsNullOrWhiteSpace(value) ? string.Empty : For(realmId.Trim() + "|" + value.Trim());

    /// <summary>
    /// The realm-qualified keys the caller can be matched under: its own realm-qualified identifiers plus every bare identifier form
    /// qualified with the realm claim the authentication pipeline set. Empty when the caller carries no realm-qualified identity.
    /// </summary>
    public static IReadOnlyCollection<string> ForCaller(SecurityPrincipalContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var identifiers = SeparationOfDutiesService.CollectSubjectIdentifiers(caller.Principal, caller.UserSid.Value);
        var realm = caller.Principal?.FindFirst("autheris:realm")?.Value;
        return ForIdentifiers(identifiers, realm);
    }

    /// <summary>The realm-qualified keys of <paramref name="identifiers"/>; bare forms count only with a <paramref name="realmId"/>.</summary>
    public static IReadOnlyCollection<string> ForIdentifiers(IEnumerable<string> identifiers, string? realmId)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in identifiers)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            var key = identifier.Contains('|', StringComparison.Ordinal) ? For(identifier) : Qualified(realmId, identifier);
            if (key.Length > 0 && key.Length <= MaxLength)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>Splits a stored realm-qualified reference into realm and key.</summary>
    public static bool TrySplit(string? reference, out string realmId, out string key)
    {
        realmId = string.Empty;
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var sep = reference.IndexOf('|', StringComparison.Ordinal);
        if (sep <= 0 || sep == reference.Length - 1)
        {
            return false;
        }

        realmId = reference[..sep].Trim();
        key = reference[(sep + 1)..].Trim();
        return realmId.Length > 0 && key.Length > 0;
    }
}
