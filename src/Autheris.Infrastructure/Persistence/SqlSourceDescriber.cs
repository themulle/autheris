using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Describes a configured SQL data source (host, port, dialect, authentication path) without connecting and without secrets:
/// user, password, database and the rest of the connection string are never part of the result.
/// </summary>
public sealed class SqlSourceDescriber : ISqlSourceDescriber
{
    public SqlSourceDescription Describe(DataSourceConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string dialect = DataSourceProvider.TryResolveDialect(options.Provider, out var resolved) ? resolved.ToString() : (options.Provider ?? string.Empty);
        bool sqlServer = resolved == DatabaseDialect.SqlServer;
        var (host, port) = ParseHostAndPort(options.ConnectionString);
        string authPath = sqlServer ? SqlServerWindowsAuthConfigurator.DescribeAuthPath(options) : "n/a";
        return new SqlSourceDescription(host, port, dialect, authPath, DescribeLogin(options, sqlServer));
    }

    /// <summary>Effective login name without the password: the configured Windows account, the SQL login or a marker for ambient credentials.</summary>
    internal static string? DescribeLogin(DataSourceConnectionOptions options, bool sqlServer)
    {
        if (sqlServer && options.WindowsAuth is { Mode: not SqlServerWindowsAuthMode.None } winAuth)
        {
            var (domain, user) = SqlServerWindowsAuthConfigurator.NormalizeDomainAndUser(winAuth.Domain, winAuth.Username);
            if (user is null)
            {
                return "(account from NTLM_USER_FILE)";
            }

            return domain is null ? user : $"{domain}\\{user}";
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = options.ConnectionString };
            foreach (var key in new[] { "User ID", "UID", "User Id", "Username", "User" })
            {
                if (builder.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            if (sqlServer && builder.TryGetValue("Integrated Security", out var integrated) &&
                integrated is string flag && (flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag.Equals("sspi", StringComparison.OrdinalIgnoreCase)))
            {
                return "(ambient credentials)";
            }
        }
        catch (ArgumentException)
        {
            // malformed connection string: login unknown
        }

        return null;
    }

    internal static (string Host, int? Port) ParseHostAndPort(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return (string.Empty, null);
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            string? server = null;
            foreach (var key in new[] { "Data Source", "Server", "Address", "Addr", "Network Address", "Host" })
            {
                if (builder.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text))
                {
                    server = text.Trim();
                    break;
                }
            }

            if (server is null)
            {
                return (string.Empty, null);
            }

            int colon = server.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 && colon is > 0 and <= 5 && server.AsSpan(0, colon).IndexOfAnyExcept("tcpnamdilTCPNAMDIL".AsSpan()) < 0)
            {
                server = server[(colon + 1)..]; // protocol prefix: tcp:, np:, lpc:, admin:
            }

            int? port = null;
            int comma = server.IndexOf(',', StringComparison.Ordinal);
            if (comma >= 0)
            {
                if (int.TryParse(server.AsSpan(comma + 1).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p))
                {
                    port = p;
                }

                server = server[..comma];
            }

            int backslash = server.IndexOf('\\', StringComparison.Ordinal);
            if (backslash >= 0)
            {
                server = server[..backslash];
            }

            return (server.Trim(), port);
        }
        catch (ArgumentException)
        {
            return (string.Empty, null);
        }
    }
}
