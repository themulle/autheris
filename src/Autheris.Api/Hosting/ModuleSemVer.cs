namespace Autheris.Api.Hosting;

using System;
using System.Globalization;

/// <summary>
/// Strict SemVer 2.0 value used for module version floors. Build metadata is ignored for precedence;
/// a pre-release sorts below its release.
/// </summary>
internal readonly struct ModuleSemVer : IComparable<ModuleSemVer>
{
    private readonly string[] _prerelease;

    private ModuleSemVer(ulong major, ulong minor, ulong patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public ulong Major { get; }

    public ulong Minor { get; }

    public ulong Patch { get; }

    public static bool TryParse(string? text, out ModuleSemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim())
        {
            return false;
        }

        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var withoutBuild = plus >= 0 ? text[..plus] : text;
        if (plus >= 0 && !AreValidIdentifiers(text[(plus + 1)..], numericLeadingZeroAllowed: true))
        {
            return false;
        }

        var dash = withoutBuild.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? withoutBuild[..dash] : withoutBuild;
        var pre = dash >= 0 ? withoutBuild[(dash + 1)..] : string.Empty;
        if (dash >= 0 && !AreValidIdentifiers(pre, numericLeadingZeroAllowed: false))
        {
            return false;
        }

        var parts = core.Split('.');
        if (parts.Length != 3 ||
            !TryParseNumeric(parts[0], out var major) ||
            !TryParseNumeric(parts[1], out var minor) ||
            !TryParseNumeric(parts[2], out var patch))
        {
            return false;
        }

        version = new ModuleSemVer(major, minor, patch, dash >= 0 ? pre.Split('.') : []);
        return true;
    }

    public int CompareTo(ModuleSemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        c = Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
        {
            return other._prerelease.Length.CompareTo(_prerelease.Length);
        }

        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var a = _prerelease[i];
            var b = other._prerelease[i];
            var aNum = ulong.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNum = ulong.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
            c = (aNum, bNum) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a, b),
            };
            if (c != 0)
            {
                return c;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static bool TryParseNumeric(string part, out ulong value)
    {
        value = 0;
        if (part.Length == 0 || (part.Length > 1 && part[0] == '0'))
        {
            return false;
        }

        return ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool AreValidIdentifiers(string text, bool numericLeadingZeroAllowed)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var id in text.Split('.'))
        {
            if (id.Length == 0)
            {
                return false;
            }

            var allDigits = true;
            foreach (var ch in id)
            {
                var digit = ch is >= '0' and <= '9';
                allDigits &= digit;
                if (!digit && !(ch is >= 'a' and <= 'z') && !(ch is >= 'A' and <= 'Z') && ch != '-')
                {
                    return false;
                }
            }

            if (!numericLeadingZeroAllowed && allDigits && id.Length > 1 && id[0] == '0')
            {
                return false;
            }
        }

        return true;
    }
}
