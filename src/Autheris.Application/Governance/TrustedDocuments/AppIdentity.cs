namespace Autheris.Application.Governance.TrustedDocuments;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

/// <summary>
/// The identity an app-scoped trusted document set belongs to: <c>appId/version</c> (Issue #281, SEC-281-5). Both parts are validated by
/// source-generated, culture-invariant regexes with a match timeout, always behind a length check.
/// </summary>
public readonly partial record struct AppIdentity(string AppId, string Version)
{
    public const int MaxAppIdLength = 64;
    public const int MaxVersionLength = 64;

    /// <summary>Longest valid header value: appId, separator, version.</summary>
    public const int MaxLength = MaxAppIdLength + 1 + MaxVersionLength;

    public static bool IsValidAppId(string? value) =>
        value is { Length: > 0 and <= MaxAppIdLength } && AppIdPattern().IsMatch(value);

    public static bool IsValidVersion(string? value) =>
        value is { Length: > 0 and <= MaxVersionLength } && VersionPattern().IsMatch(value);

    /// <summary>Parses exactly <c>&lt;appId&gt;/&lt;version&gt;</c>; any other shape (no or two separators, empty part, illegal character, oversize) fails.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out AppIdentity identity)
    {
        identity = default;
        if (value is not { Length: > 2 and <= MaxLength })
        {
            return false;
        }

        int slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash != value.LastIndexOf('/'))
        {
            return false;
        }

        var appId = value[..slash];
        var version = value[(slash + 1)..];
        if (!IsValidAppId(appId) || !IsValidVersion(version))
        {
            return false;
        }

        identity = new AppIdentity(appId, version);
        return true;
    }

    public override string ToString() => $"{AppId}/{Version}";

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AppIdPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._+-]{0,63}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex VersionPattern();
}
