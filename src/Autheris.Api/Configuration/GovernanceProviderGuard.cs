using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Autheris.Api.Configuration;

/// <summary>
/// Validates at host startup that the configured governance database provider and connection string
/// target a supported enterprise database engine (PostgreSQL or SQL Server) and fail closed otherwise.
/// </summary>
public static partial class GovernanceProviderGuard
{
    public const string UnsupportedProviderMessage =
        "Governance provider unsupported (OD-1). See docs/operations/migration-simplification.md.";

    private static readonly string[] ProviderKeys =
    [
        "GovernanceDb:Provider",
        "Gateway:GovernanceDb:Provider",
        "Governance:Provider"
    ];

    private static readonly string[] ConnectionStringKeys =
    [
        "GovernanceDb:ConnectionString",
        "Gateway:GovernanceDb:ConnectionString",
        "Governance:ConnectionString",
        "GovernanceDb:MigrationConnectionString",
        "Gateway:GovernanceDb:MigrationConnectionString"
    ];

    [GeneratedRegex(@"(?:^|;)\s*Mode\s*=\s*Memory\s*(?:;|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModeMemoryRegex();

    [GeneratedRegex(@"(?:^|;)\s*Filename\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilenameKeyRegex();

    [GeneratedRegex(@"(?:^|;)\s*Data\s*Source\s*=\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataSourceRegex();

    public static void ThrowIfUnsupported(IConfiguration cfg, IHostEnvironment? env = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _ = env;

        var hasNonEmptyConnectionString = false;
        foreach (var key in ConnectionStringKeys)
        {
            var rawConnectionString = cfg[key];
            if (string.IsNullOrWhiteSpace(rawConnectionString))
            {
                continue;
            }

            if (key is "GovernanceDb:ConnectionString" or "Gateway:GovernanceDb:ConnectionString")
            {
                hasNonEmptyConnectionString = true;
            }

            if (IsSqliteConnectionString(rawConnectionString))
            {
                throw new InvalidOperationException(UnsupportedProviderMessage);
            }
        }

        var hasProvider = false;
        foreach (var key in ProviderKeys)
        {
            var rawProvider = cfg[key];
            if (string.IsNullOrWhiteSpace(rawProvider))
            {
                continue;
            }

            hasProvider = true;
            var provider = rawProvider.Trim();
            if (!IsSupportedProvider(provider))
            {
                throw new InvalidOperationException(UnsupportedProviderMessage);
            }
        }

        if (!hasProvider && !hasNonEmptyConnectionString)
        {
            throw new InvalidOperationException(UnsupportedProviderMessage);
        }
    }

    private static bool IsSupportedProvider(string provider) =>
        string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSqliteConnectionString(string connectionString)
    {
        if (connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ModeMemoryRegex().IsMatch(connectionString) || FilenameKeyRegex().IsMatch(connectionString))
        {
            return true;
        }

        foreach (Match match in DataSourceRegex().Matches(connectionString))
        {
            var dataSource = match.Groups[1].Value.Trim().Trim('"', '\'');
            if (dataSource.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                dataSource.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                dataSource.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
