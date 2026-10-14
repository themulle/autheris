namespace Autheris.Infrastructure.Databricks;

using System.Text.RegularExpressions;

/// <summary>SEC-ADG-09 item 2: the workspace host is one exact DNS name (no scheme, port, path, user info, wildcard or IP literal).</summary>
public static partial class DatabricksHostPolicy
{
    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostRegex();

    public static bool IsValidHost(string? host) => !string.IsNullOrEmpty(host) && HostRegex().IsMatch(host);
}
