namespace Autheris.Domain.Common;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Autheris.Domain.Identity;

/// <summary>Canonical SID text form checks shared by every identity source.</summary>
public static class SidFormat
{
    public const int MaxLength = 184;
    public const string EntraCloudSidPrefix = "S-1-12-1-";
    public const string RealmGroupKeyPrefix = "realm:";
    public const int MaxGroupKeyLength = MaxLength;

    /// <summary>
    /// True for <c>S-1-{authority 1-2 digits}(-{subauthority 1-10 digits}){1,15}</c>, ASCII digits only, at most
    /// <see cref="MaxLength"/> characters. A linear scan without a regular expression, so no input can cause backtracking.
    /// </summary>
    public static bool IsCanonical(string? value) =>
        IsCanonicalCore(value, allowLowerCasePrefix: false);

    /// <summary>
    /// Case-insensitive Windows SID form check used only for SID equality normalization (<c>S-1-...</c> / <c>s-1-...</c>).
    /// </summary>
    public static bool IsCanonicalIgnoreCase(string? value) =>
        IsCanonicalCore(value, allowLowerCasePrefix: true);

    private static bool IsCanonicalCore(string? value, bool allowLowerCasePrefix)
    {
        var prefixMatch = value is not null
            && value.Length <= MaxLength
            && value.StartsWith("S-1-", allowLowerCasePrefix ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (!prefixMatch)
        {
            return false;
        }

        var segments = 0;
        var digits = 0;
        for (var i = 4; i <= value!.Length; i++)
        {
            if (i == value.Length || value[i] == '-')
            {
                // Segment 0 is the authority (1-2 digits); the others are subauthorities (1-10 digits).
                if (digits == 0 || digits > (segments == 0 ? 2 : 10))
                {
                    return false;
                }

                segments++;
                digits = 0;
                continue;
            }

            if (value[i] is < '0' or > '9')
            {
                return false;
            }

            digits++;
        }

        return segments is >= 2 and <= 16;
    }

    private const int MaxSubAuthorities = 15;

    /// <summary>
    /// Cloud SID <c>S-1-12-1-{u0}-{u1}-{u2}-{u3}</c> of an Entra object id: the four little-endian <see cref="uint"/> of
    /// <see cref="Guid.ToByteArray()"/>. Never throws.
    /// </summary>
    public static bool TryFromEntraObjectId(Guid objectId, out Sid sid)
    {
        sid = default;
        Span<byte> bytes = stackalloc byte[16];
        if (objectId == Guid.Empty || !objectId.TryWriteBytes(bytes, bigEndian: false, out var written) || written != 16)
        {
            return false;
        }

        sid = new Sid(string.Create(CultureInfo.InvariantCulture,
            $"S-1-12-1-{BinaryPrimitives.ReadUInt32LittleEndian(bytes)}-{BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..])}-{BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..])}-{BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..])}"));
        return true;
    }

    public static bool TryFromEntraObjectId(Guid objectId, IdentityRealm? realm, string? tokenIssuer, out Sid sid, string? tokenTenantId = null)
    {
        sid = default;
        return HasTenantBoundEntraIssuers(realm, tokenIssuer, tokenTenantId) && TryFromEntraObjectId(objectId, out sid);
    }

    public static bool IsEntraCloudSid(string? value) => IsCanonical(value) && value!.StartsWith(EntraCloudSidPrefix, StringComparison.Ordinal);
    public static bool IsWindowsAdDomainSid(string? value) => IsCanonical(value) && !IsEntraCloudSid(value);
    public static bool IsEntraRealm(IdentityRealmKind kind) => kind is IdentityRealmKind.EntraWorkforce or IdentityRealmKind.EntraExternalId;
    public static bool UsesWorkforceAdSids(IdentityRealmKind kind) => kind is IdentityRealmKind.EntraWorkforce or IdentityRealmKind.Adfs or IdentityRealmKind.Kerberos;

    public static bool IsTenantBoundEntraIssuer(string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer)) return false;
        var trimmed = issuer.Trim();
        if (trimmed.AsSpan().IndexOfAny("*{}?#") >= 0
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (IsWellKnownEntraAuthorityHost(uri.Host) && (segments.Length == 0 || segments[0].Equals("v2.0", StringComparison.OrdinalIgnoreCase)))
            return false;
        foreach (var seg in segments)
            if (seg.Equals("common", StringComparison.OrdinalIgnoreCase)
                || seg.Equals("organizations", StringComparison.OrdinalIgnoreCase)
                || seg.Equals("consumers", StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private static bool IsWellKnownEntraAuthorityHost(string host) =>
        host.Equals("sts.windows.net", StringComparison.OrdinalIgnoreCase)
        || host.Equals("login.windows.net", StringComparison.OrdinalIgnoreCase)
        || host.StartsWith("login.microsoftonline.", StringComparison.OrdinalIgnoreCase)
        || host.Equals("login.partner.microsoftonline.cn", StringComparison.OrdinalIgnoreCase)
        || host.Equals("ciamlogin.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".ciamlogin.com", StringComparison.OrdinalIgnoreCase);

    public static bool HasTenantBoundEntraIssuers(IdentityRealm? realm, string? tokenIssuer = null, string? tokenTenantId = null)
    {
        if (realm is null || !IsEntraRealm(realm.Kind) || realm.Issuers.Count == 0 || !realm.Issuers.All(IsTenantBoundEntraIssuer))
            return false;

        if (!string.IsNullOrWhiteSpace(tokenIssuer))
        {
            var normalized = tokenIssuer.Trim().TrimEnd('/');
            if (!realm.Issuers.Any(i => string.Equals(i.Trim().TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))) return false;
            return string.IsNullOrWhiteSpace(tokenTenantId)
                || !TryExtractEntraTenantGuid(normalized, out var issuerTid)
                || string.Equals(issuerTid.ToString("D"), tokenTenantId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(tokenTenantId)) return true;
        var guidIssuers = realm.Issuers.Select(i => (Ok: TryExtractEntraTenantGuid(i, out var tid), Tid: tid)).Where(x => x.Ok).ToList();
        return guidIssuers.Count == 0 || guidIssuers.Any(x => string.Equals(x.Tid.ToString("D"), tokenTenantId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryExtractEntraTenantGuid(string issuer, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (!Uri.TryCreate(issuer.Trim(), UriKind.Absolute, out var uri)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && Guid.TryParseExact(segments[0], "D", out tenantId) && tenantId != Guid.Empty;
    }

    public static bool TryFormatRealmGroupKey(string realmId, string groupId, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(realmId) || string.IsNullOrWhiteSpace(groupId)) return false;
        var candidate = $"{RealmGroupKeyPrefix}{Uri.EscapeDataString(realmId.Trim())}:{Uri.EscapeDataString(groupId.Trim())}";
        if (candidate.Length > MaxGroupKeyLength) return false;
        key = candidate;
        return true;
    }

    public static string FormatRealmGroupKey(string realmId, string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        return TryFormatRealmGroupKey(realmId, groupId, out var key)
            ? key : throw new ArgumentException("Realm-bound group key exceeds maximum supported length.", nameof(groupId));
    }

    public static bool IsRealmGroupKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxGroupKeyLength || !value.StartsWith(RealmGroupKeyPrefix, StringComparison.Ordinal))
            return false;
        var rest = value.AsSpan(RealmGroupKeyPrefix.Length);
        var colon = rest.IndexOf(':');
        if (colon <= 0 || colon >= rest.Length - 1 || rest[(colon + 1)..].IndexOf(':') >= 0) return false;
        ReadOnlySpan<char> rawRealm = rest[..colon], rawGroup = rest[(colon + 1)..];
        return IsValidEscapedSegment(rawRealm) && IsValidEscapedSegment(rawGroup)
            && TryFormatRealmGroupKey(Uri.UnescapeDataString(rawRealm.ToString()), Uri.UnescapeDataString(rawGroup.ToString()), out var canonical)
            && string.Equals(value, canonical, StringComparison.Ordinal);
    }

    public static bool IsGroupSid(string? value) => IsCanonical(value) || IsRealmGroupKey(value);
    public static bool IsGroupOrRealmGroupIdentifier(string? value) => IsGroupSid(value);

    public static bool IsValidGroupIdentifierForRealm(
        string? value,
        IdentityRealmKind? realmKind,
        string? realmId = null,
        bool allowEntraCloudSid = false)
    {
        if (!IsGroupSid(value))
        {
            return false;
        }

        if (IsRealmGroupKey(value))
        {
            return string.IsNullOrWhiteSpace(realmId) || MatchesRealmGroupKey(value, realmId);
        }

        if (realmKind is not { } kind)
        {
            return true;
        }

        return UsesWorkforceAdSids(kind) && (!IsEntraCloudSid(value) || allowEntraCloudSid);
    }
    public static bool MatchesRealmGroupKey(string? value, string realmId) =>
        IsRealmGroupKey(value) && !string.IsNullOrWhiteSpace(realmId)
        && value!.StartsWith($"{RealmGroupKeyPrefix}{Uri.EscapeDataString(realmId.Trim())}:", StringComparison.Ordinal);

    private static bool IsValidEscapedSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty) return false;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
            if (c == '%' && (i + 2 >= segment.Length || !Uri.IsHexDigit(segment[i + 1]) || !Uri.IsHexDigit(segment[i + 2]))) return false;
            if (c == '%') i += 2;
        }
        return !string.IsNullOrWhiteSpace(Uri.UnescapeDataString(segment.ToString()));
    }

    /// <summary>
    /// Parses the binary SID form of AD <c>objectSid</c>/<c>tokenGroups</c> (revision 1, 1..15 sub-authorities, exact length,
    /// 48-bit big-endian authority below 100, little-endian sub-authorities). Never throws; any malformed input yields false.
    /// </summary>
    public static bool TryFromBinary(ReadOnlySpan<byte> binary, out Sid sid)
    {
        sid = default;
        if (binary.Length < 12 || binary[0] != 1)
        {
            return false;
        }

        int count = binary[1];
        if (count is < 1 or > MaxSubAuthorities || binary.Length != 8 + 4 * count)
        {
            return false;
        }

        var authority = ((ulong)binary[2] << 40) | ((ulong)binary[3] << 32) | ((ulong)binary[4] << 24)
                        | ((ulong)binary[5] << 16) | ((ulong)binary[6] << 8) | binary[7];
        if (authority > 99)
        {
            return false;
        }

        var text = new StringBuilder(32).Append("S-1-").Append(authority.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < count; i++)
        {
            text.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(binary[(8 + 4 * i)..]).ToString(CultureInfo.InvariantCulture));
        }

        var value = text.ToString();
        if (!IsCanonical(value))
        {
            return false;
        }

        sid = new Sid(value);
        return true;
    }

    /// <summary>
    /// Built-in (<c>S-1-5-32-*</c>), service (<c>S-1-5-18/19/20</c>) and well-known privileged domain identities (RID 500, 512, 516, 518, 519).
    /// Ordinal, culture-insensitive.
    /// </summary>
    public static bool IsPrivileged(Sid sid)
    {
        var value = sid.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.StartsWith("S-1-5-32-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "S-1-5-32", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "S-1-5-18", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "S-1-5-19", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "S-1-5-20", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!value.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rid = value.AsSpan(value.LastIndexOf('-', StringComparison.Ordinal) + 1);
        return rid.SequenceEqual("500") || rid.SequenceEqual("512") || rid.SequenceEqual("516")
               || rid.SequenceEqual("518") || rid.SequenceEqual("519");
    }

    /// <summary>Binary SID form of a canonical SID (for an <c>objectSid</c> equality filter). Never throws.</summary>
    public static bool TryToBinary(string? value, out byte[] binary)
    {
        binary = [];
        if (!IsCanonical(value))
        {
            return false;
        }

        var parts = value!.Split('-');
        var authority = byte.Parse(parts[2], CultureInfo.InvariantCulture);
        var count = parts.Length - 3;
        var bytes = new byte[8 + 4 * count];
        bytes[0] = 1;
        bytes[1] = (byte)count;
        bytes[7] = authority;
        for (var i = 0; i < count; i++)
        {
            if (!uint.TryParse(parts[3 + i], NumberStyles.None, CultureInfo.InvariantCulture, out var sub))
            {
                return false;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + 4 * i), sub);
        }

        binary = bytes;
        return true;
    }
}
