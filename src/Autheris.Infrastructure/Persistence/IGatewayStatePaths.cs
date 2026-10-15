using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Resolves the local gateway state locations from <see cref="StateOptions"/> (WP-ARC-B-13).</summary>
public interface IGatewayStatePaths
{
    /// <summary>Full path of the state directory.</summary>
    string StateDirectory { get; }

    /// <summary>Full path of the SQLite governance database file inside <see cref="StateDirectory"/>.</summary>
    string GovernanceDatabaseFile { get; }

    /// <summary>
    /// The effective SQLite governance connection string: an explicitly configured value wins; otherwise Development/Test use a private
    /// in-memory database and every other environment the governance database file in the state directory.
    /// </summary>
    string ResolveGovernanceConnectionString(string? configured);
}

/// <inheritdoc />
public sealed class GatewayStatePaths(IOptions<StateOptions> options, IHostEnvironment environment) : IGatewayStatePaths
{
    private const string GovernanceDatabaseFileName = "governance.db";

    // A relative directory is resolved against the application content root, not the (arbitrary) working directory.
    public string StateDirectory { get; } = Path.IsPathRooted(environment.ContentRootPath)
        ? Path.GetFullPath(options.Value.StateDirectory, environment.ContentRootPath)
        : Path.GetFullPath(options.Value.StateDirectory);

    public string GovernanceDatabaseFile => Path.Combine(StateDirectory, GovernanceDatabaseFileName);

    public string ResolveGovernanceConnectionString(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var isDevOrTest = string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(environment.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase);
        return isDevOrTest
            ? $"Data Source=governance_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            : $"Data Source={GovernanceDatabaseFile}";
    }

    /// <summary>The default audit chain anchor file: next to the governance database.</summary>
    public static string AuditAnchorFileFor(string governanceDbFile) => governanceDbFile + ".audit-anchor.json";

    /// <summary>The configured directory when set, otherwise <paramref name="fallback"/> (used by the Development presets).</summary>
    public static string ResolveDirectory(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured;
}

/// <summary>
/// Validates <see cref="StateOptions"/> for the SQLite governance provider: the directory must be usable, and in a container it must not be
/// relative or the read-only application root (<c>/app</c>).
/// </summary>
public sealed class StateOptionsValidator(IOptions<GatewayOptions> gateway, Func<string, string?>? getEnvironmentVariable = null) : IValidateOptions<StateOptions>
{
    private readonly Func<string, string?> _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

    public ValidateOptionsResult Validate(string? name, StateOptions options)
    {
        // The state directory only matters for the SQLite provider; PostgreSQL and SQL Server keep their state in the database server.
        if (!Autheris.Domain.Common.DataSourceProvider.Is(gateway.Value.GovernanceDb.Provider, Autheris.Domain.Common.DatabaseDialect.Sqlite))
        {
            return ValidateOptionsResult.Success;
        }

        var directory = options.StateDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return ValidateOptionsResult.Fail("Gateway:State:StateDirectory must not be empty.");
        }

        if (directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return ValidateOptionsResult.Fail("Gateway:State:StateDirectory contains invalid path characters.");
        }

        var containerFlag = _getEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        var inContainer = string.Equals(containerFlag, "true", StringComparison.OrdinalIgnoreCase) || containerFlag == "1";
        if (inContainer)
        {
            if (!Path.IsPathRooted(directory))
            {
                return ValidateOptionsResult.Fail("Gateway:State:StateDirectory must be an absolute path in a container (for example '/app/data' on a mounted volume).");
            }

            var normalized = directory.Replace('\\', '/').TrimEnd('/');
            if (normalized.Equals("/app", StringComparison.OrdinalIgnoreCase))
            {
                return ValidateOptionsResult.Fail("Gateway:State:StateDirectory must not be the read-only application root '/app' in a container; use a mounted volume such as '/app/data'.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
