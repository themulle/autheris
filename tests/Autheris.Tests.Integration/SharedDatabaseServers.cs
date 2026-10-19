namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

/// <summary>
/// One PostgreSQL and one SQL Server container per test process (WP-TS-01). Starting a container takes about ten seconds, so the
/// database tests share the servers and isolate themselves with a fresh database each (<see cref="PostgreSqlTestDatabase"/>,
/// <see cref="SqlServerTestDatabase"/>). A failed start is cached, so every consumer sees the same failure instead of retrying.
/// <para>
/// Cleanup: under CI the resource reaper (Ryuk) removes the containers when the run ends. On a developer machine Ryuk is usually
/// disabled and the test host is killed before a process-exit handler finishes, so the containers are reused across runs instead
/// (Testcontainers reuse): at most one container per engine stays running, and later runs skip the start.
/// </para>
/// </summary>
internal static class SharedDatabaseServers
{
    private static readonly bool Reuse = !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    private static readonly Lazy<Task<PostgreSqlContainer>> PostgreSql =
        new(() => StartAsync(new PostgreSqlBuilder("postgres:16-alpine")
            .WithReuse(Reuse)
            .WithLabel("autheris.tests", "shared-postgresql")
            .Build()));

    private static readonly Lazy<Task<MsSqlContainer>> SqlServer =
        new(() => StartAsync(new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithReuse(Reuse)
            .WithLabel("autheris.tests", "shared-sqlserver")
            .Build()));

    /// <summary>Connection string of the shared PostgreSQL server (superuser, maintenance database).</summary>
    public static async Task<string> PostgreSqlServerAsync()
    {
        var env = Environment.GetEnvironmentVariable("AUTHERIS_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return (await PostgreSql.Value).GetConnectionString();
    }

    /// <summary>Connection string of the shared SQL Server (sa, master).</summary>
    public static async Task<string> SqlServerServerAsync()
    {
        var env = Environment.GetEnvironmentVariable("AUTHERIS_TEST_MSSQL");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return (await SqlServer.Value).GetConnectionString();
    }

    private static async Task<T> StartAsync<T>(T container) where T : IContainer
    {
        try
        {
            await container.StartAsync();
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }

        return container;
    }
}
