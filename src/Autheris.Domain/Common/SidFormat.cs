namespace Autheris.Domain.Common;

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
}
