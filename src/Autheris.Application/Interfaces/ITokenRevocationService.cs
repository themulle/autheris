namespace Autheris.Application.Interfaces;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// SEC M-14 (GAP-B): Token revocation store. A revocation entry is keyed by a token id (<c>jti</c>) or a subject
/// (<c>GetUserSid()</c> / <c>sub</c>) and revokes every token whose <c>iat</c> is not
/// later than the revocation time. Tokens without <c>iat</c> are treated as revoked once an entry matches.
/// </summary>
public interface ITokenRevocationService
{
    /// <summary>
    /// Returns true if the principal's token was revoked (by <c>jti</c> or by subject).
    /// </summary>
    ValueTask<bool> IsRevokedAsync(ClaimsPrincipal principal, CancellationToken ct = default);

    /// <summary>
    /// Revokes the token id or all tokens of the subject issued up to now. The entry is kept until
    /// <paramref name="until"/>, which should be the (maximum) expiry of the affected tokens.
    /// </summary>
    Task RevokeAsync(string subjectOrJti, DateTimeOffset until, CancellationToken ct = default);
}

/// <summary>
/// SEC M-14 (GAP-B): Shared key and timestamp handling for <see cref="ITokenRevocationService"/> implementations.
/// </summary>
public static class TokenRevocationKeys
{
    /// <summary>
    /// Normalizes a revocation key (subjects/SIDs are case-insensitive; a case collision of two jti values
    /// can only cause over-revocation).
    /// </summary>
    public static string Normalize(string subjectOrJti)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectOrJti);
        return subjectOrJti.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Returns the normalized keys to look up for a principal: <c>jti</c>, the user SID and <c>sub</c> (distinct).
    /// </summary>
    public static IReadOnlyList<string> GetLookupKeys(ClaimsPrincipal? principal)
    {
        var keys = new List<string>(3);
        if (principal == null)
        {
            return keys;
        }

        AddKey(keys, principal.FindFirst("jti")?.Value);
        AddKey(keys, principal.GetUserSid()?.Value);
        AddKey(keys, principal.FindFirst("sub")?.Value);
        return keys;
    }

    /// <summary>
    /// Reads the <c>iat</c> claim (Unix seconds).
    /// </summary>
    public static DateTimeOffset? GetIssuedAt(ClaimsPrincipal? principal)
    {
        var iat = principal?.FindFirst("iat")?.Value;
        if (!string.IsNullOrWhiteSpace(iat) &&
            long.TryParse(iat, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
            seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return null;
    }

    /// <summary>
    /// A token is covered by a revocation entry if it was issued at or before the revocation time
    /// (or if its issue time is unknown).
    /// </summary>
    public static bool IsCovered(DateTimeOffset? issuedAt, DateTimeOffset revokedAt)
        => issuedAt is not { } iat || iat <= revokedAt;

    private static void AddKey(List<string> keys, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = Normalize(value);
        if (!keys.Contains(normalized))
        {
            keys.Add(normalized);
        }
    }
}
