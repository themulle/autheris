namespace Autheris.Application.Security;

using System;
using System.Text.RegularExpressions;

public static class SecretScrubber
{
    private static readonly Regex BearerRegex = new(@"(Bearer\s+)[A-Za-z0-9\-\._~\+\/]+=*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ApiKeyRegex = new(@"((?:api[_-]?key|token|secret|password|pwd)\s*[:=]\s*)(['""]?[A-Za-z0-9\-\._~\+\/]+=*['""]?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ConnectionStringPasswordRegex = new(@"((?:password|pwd)\s*=\s*)([^;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Redact(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var result = BearerRegex.Replace(input, "$1***REDACTED***");
        result = ApiKeyRegex.Replace(result, "$1***REDACTED***");
        result = ConnectionStringPasswordRegex.Replace(result, "$1***REDACTED***");
        return result;
    }

    public static string Scrub(string? input) => Redact(input);
}
