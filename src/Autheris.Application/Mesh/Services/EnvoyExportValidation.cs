namespace Autheris.Application.Mesh.Services;

using System;
using System.Buffers;

/// <summary>
/// Deterministic, allocation-free replacements for the former wall-clock-bounded regex checks (ARCH-MNT-27).
/// ASCII only, culture-free, never throws. Deliberate tightening versus the former regexes: the old <c>$</c>
/// accepted one trailing <c>\n</c>; these checks reject it. The same tightening applies to <see cref="IsVersionSegment"/> (no effect there: segments come from a split path).
/// </summary>
internal static class EnvoyExportValidation
{
    private const int MaxDnsLabelLength = 63;
    private const int MaxAuthzPathLength = 201;

    private static readonly SearchValues<char> LowerAlnum = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789");
    private static readonly SearchValues<char> LabelInterior = SearchValues.Create("-abcdefghijklmnopqrstuvwxyz0123456789");
    private static readonly SearchValues<char> PathChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789/_.-");
    private static readonly SearchValues<char> AsciiDigits = SearchValues.Create("0123456789");

    /// <summary>Former <c>^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$</c>.</summary>
    public static bool IsDnsLabel(ReadOnlySpan<char> value) =>
        value.Length is >= 1 and <= MaxDnsLabelLength
        && LowerAlnum.Contains(value[0])
        && LowerAlnum.Contains(value[^1])
        && !value.ContainsAnyExcept(LabelInterior);

    /// <summary>Former <c>^/[A-Za-z0-9/_.-]{0,200}$</c>.</summary>
    public static bool IsAuthzPath(ReadOnlySpan<char> value) =>
        value.Length is >= 1 and <= MaxAuthzPathLength
        && value[0] == '/'
        && !value[1..].ContainsAnyExcept(PathChars);

    /// <summary>Former case-insensitive <c>^v[0-9]+$</c>.</summary>
    public static bool IsVersionSegment(ReadOnlySpan<char> value) =>
        value.Length >= 2
        && (value[0] == 'v' || value[0] == 'V')
        && !value[1..].ContainsAnyExcept(AsciiDigits);
}
