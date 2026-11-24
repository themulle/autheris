namespace Autheris.Application.Governance.Paging;

using System;
using System.Globalization;
using System.Text;
using Autheris.Domain.Model;

/// <summary>
/// Keyset cursor over the requests of a tenant in the order of the request page query: newest first by
/// <c>COALESCE(updated_at, requested_at)</c>, then by id descending. The cursor only encodes a position and carries no authority.
/// </summary>
public static class ConsentRequestCursor
{
    /// <summary>Upper bound for an encoded cursor; longer input is rejected without decoding.</summary>
    public const int MaxLength = 256;

    /// <summary>Encodes the position of <paramref name="request"/> so the next page starts strictly after it.</summary>
    public static string For(ConsentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Encode((request.UpdatedAt ?? request.RequestedAt).ToUniversalTime(), request.Id);
    }

    public static string Encode(DateTimeOffset updatedAt, Guid id)
    {
        var raw = string.Concat(updatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), "|", id.ToString("D"));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>Returns false for a missing, oversized or malformed cursor.</summary>
    public static bool TryDecode(string? cursor, out DateTimeOffset updatedAt, out Guid id)
    {
        updatedAt = default;
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaxLength)
        {
            return false;
        }

        try
        {
            var padded = cursor.Trim().Replace('-', '+').Replace('_', '/');
            var mod = padded.Length % 4;
            if (mod > 0)
            {
                padded += new string('=', 4 - mod);
            }

            var text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var sep = text.IndexOf('|', StringComparison.Ordinal);
            if (sep <= 0 ||
                !DateTimeOffset.TryParse(text.AsSpan(0, sep), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts) ||
                !Guid.TryParse(text.AsSpan(sep + 1), out var parsedId))
            {
                return false;
            }

            updatedAt = ts.ToUniversalTime();
            id = parsedId;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
