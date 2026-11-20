namespace Autheris.Application.Events.Services;

using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Custom header handling of CDC webhook subscriptions. Header values can carry tokens, so the endpoint stores them protected
/// (a single entry with the key <see cref="ProtectedKey"/> holding the protected JSON of the real dictionary) and never returns them.
/// </summary>
public static class CdcCustomHeaders
{
    public const string ProtectedKey = "enc:v1";
    public const int MaxCount = 20;
    public const int MaxNameLength = 128;
    public const int MaxValueLength = 2048;

    public static bool IsProtected(IReadOnlyDictionary<string, string>? headers) =>
        headers is { Count: 1 } && headers.ContainsKey(ProtectedKey);

    public static bool WithinBounds(IReadOnlyDictionary<string, string>? headers) =>
        headers is null
        || (headers.Count <= MaxCount && headers.All(h => IsValidHeaderName(h.Key) && IsValidHeaderValue(h.Value)));

    private static bool IsValidHeaderName(string? name) =>
        name is { Length: > 0 and <= MaxNameLength } && name.All(IsHttpTokenChar);

    private static bool IsValidHeaderValue(string? value) =>
        value is { Length: <= MaxValueLength } && value.All(static c => (c >= 0x20 && c != 0x7F) || c == '\t');

    private static bool IsHttpTokenChar(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}
