namespace Autheris.Domain.Common;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

/// <summary>Canonical SID text form checks shared by every identity source.</summary>
public static class SidFormat
{
    public const int MaxLength = 184;

    /// <summary>
    /// True for <c>S-1-{authority 1-2 digits}(-{subauthority 1-10 digits}){1,15}</c>, ASCII digits only, at most
    /// <see cref="MaxLength"/> characters. A linear scan without a regular expression, so no input can cause backtracking.
    /// </summary>
    public static bool IsCanonical(string? value)
    {
        if (value is null || value.Length > MaxLength || !value.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = 0;
        var digits = 0;
        for (var i = 4; i <= value.Length; i++)
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

        var rid = value.AsSpan(value.LastIndexOf('-') + 1);
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
