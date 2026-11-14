using System.Text.RegularExpressions;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Autheris.Api.Configuration;

/// <summary>
/// Validates at host startup that the configured governance database provider and connection string
/// target a supported enterprise database engine (PostgreSQL or SQL Server) with verified TLS outside Development,
/// and fail closed otherwise.
/// </summary>
public static partial class GovernanceProviderGuard
{
    public const string UnsupportedProviderMessage =
        "Governance provider unsupported (OD-1). Accepted providers: PostgreSql, SqlServer. See docs/operations/migration-simplification.md.";

    private static readonly string[] ProviderKeys =
    [
        "Gateway:GovernanceDb:Provider",
        "GovernanceDb:Provider",
        "Governance:Provider"
    ];

    private static readonly string[] RuntimeConnectionStringKeys =
    [
        "Gateway:GovernanceDb:ConnectionString",
        "GovernanceDb:ConnectionString",
        "Governance:ConnectionString"
    ];

    private static readonly string[] PrimaryRuntimeConnectionStringKeys =
    [
        "Gateway:GovernanceDb:ConnectionString",
        "GovernanceDb:ConnectionString"
    ];

    private static readonly string[] MigrationConnectionStringKeys =
    [
        "Gateway:GovernanceDb:MigrationConnectionString",
        "GovernanceDb:MigrationConnectionString"
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

        // Fail closed when env is null (treat as non-Development).
        var isDevelopment = env?.IsDevelopment() ?? false;

        foreach (var key in RuntimeConnectionStringKeys.Concat(MigrationConnectionStringKeys))
        {
            var rawConnectionString = cfg[key];
            if (!string.IsNullOrWhiteSpace(rawConnectionString) && IsSqliteConnectionString(rawConnectionString))
            {
                throw new InvalidOperationException(UnsupportedProviderMessage);
            }
        }

        var (effectiveProvider, hasExplicitEmptyProvider) = ResolveEffectiveProvider(cfg);
        if (effectiveProvider is null)
        {
            if (hasExplicitEmptyProvider)
            {
                throw new InvalidOperationException(UnsupportedProviderMessage);
            }

            var bound = cfg.GetSection(GatewaySections.GovernanceDb).Get<GovernanceDbOptions>();
            effectiveProvider = bound?.Provider ?? new GovernanceDbOptions().Provider;
            if (string.IsNullOrWhiteSpace(effectiveProvider) || !IsSupportedProvider(effectiveProvider.Trim()))
            {
                throw new InvalidOperationException(UnsupportedProviderMessage);
            }
        }

        var hasPrimaryRuntimeConnectionString = PrimaryRuntimeConnectionStringKeys.Any(k => !string.IsNullOrWhiteSpace(cfg[k]));
        if (!hasPrimaryRuntimeConnectionString)
        {
            throw new InvalidOperationException(UnsupportedProviderMessage);
        }

        var runtimeConnectionString = ResolveUniqueValue(
            cfg,
            RuntimeConnectionStringKeys,
            StringComparer.Ordinal,
            "Conflicting governance database connection string settings across configuration aliases (Gateway:GovernanceDb:ConnectionString, GovernanceDb:ConnectionString, Governance:ConnectionString).");

        var migrationConnectionString = ResolveUniqueValue(
            cfg,
            MigrationConnectionStringKeys,
            StringComparer.Ordinal,
            "Conflicting governance database migration connection string settings across configuration aliases (Gateway:GovernanceDb:MigrationConnectionString, GovernanceDb:MigrationConnectionString).");

        if (!isDevelopment)
        {
            ValidateTlsOrThrow(effectiveProvider, runtimeConnectionString, "GovernanceDb:ConnectionString");

            if (!string.IsNullOrWhiteSpace(migrationConnectionString))
            {
                ValidateTlsOrThrow(effectiveProvider, migrationConnectionString, "GovernanceDb:MigrationConnectionString");
            }
        }
    }

    private static (string? Provider, bool HasExplicitEmpty) ResolveEffectiveProvider(IConfiguration cfg)
    {
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? first = null;
        var hasExplicitEmpty = false;

        foreach (var key in ProviderKeys)
        {
            var raw = cfg[key];
            if (raw is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                hasExplicitEmpty = true;
                continue;
            }

            var trimmed = raw.Trim();
            if (!IsSupportedProvider(trimmed))
            {
                throw new InvalidOperationException(UnsupportedProviderMessage);
            }

            distinct.Add(trimmed);
            first ??= trimmed;
        }

        if (distinct.Count > 1)
        {
            throw new InvalidOperationException(
                "Conflicting governance database provider settings across configuration aliases (Gateway:GovernanceDb:Provider, GovernanceDb:Provider, Governance:Provider).");
        }

        return (first, hasExplicitEmpty);
    }

    private static string? ResolveUniqueValue(
        IConfiguration cfg,
        string[] keys,
        StringComparer comparer,
        string conflictMessage)
    {
        var distinct = new HashSet<string>(comparer);
        string? first = null;

        foreach (var key in keys)
        {
            var raw = cfg[key];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            distinct.Add(trimmed);
            first ??= trimmed;
        }

        if (distinct.Count > 1)
        {
            throw new InvalidOperationException(conflictMessage);
        }

        return first;
    }

    private static void ValidateTlsOrThrow(string provider, string? connectionString, string settingName)
    {
        try
        {
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls(provider, connectionString, isDevelopment: false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{settingName}: {ex.Message}", ex);
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
