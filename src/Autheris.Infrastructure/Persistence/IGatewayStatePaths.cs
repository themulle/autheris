using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Resolves the local gateway state locations from <see cref="StateOptions"/>.</summary>
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

    /// <summary>The configured directory when set, otherwise <paramref name="fallback"/> (used by the Development presets).</summary>
    public static string ResolveDirectory(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured;
}
