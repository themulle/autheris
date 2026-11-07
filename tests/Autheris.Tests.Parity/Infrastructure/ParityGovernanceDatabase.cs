using Npgsql;
using Testcontainers.PostgreSql;

namespace Autheris.Tests.Parity.Infrastructure;

/// <summary>
/// PostgreSQL governance database for the parity hosts (WP-SIMPL-11; the governance store is PostgreSQL or SQL Server only). One
/// PostgreSQL server per test process (<c>AUTHERIS_TEST_POSTGRES</c> or a Testcontainers instance); every host gets a fresh empty
/// database that the host migrates itself at startup (migration mode Apply).
/// </summary>
public static class ParityGovernanceDatabase
{
    private static readonly Lazy<Task<string>> Server = new(StartServerAsync);

    /// <summary>Creates a new empty database on the shared server and returns its connection string.</summary>
    public static async Task<string> CreateAsync()
    {
        var server = await Server.Value;
        var name = "parity_gov_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
    }

    private static async Task<string> StartServerAsync()
    {
        var env = Environment.GetEnvironmentVariable("AUTHERIS_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var reuse = !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithReuse(reuse)
            .WithLabel("autheris.tests", "parity-governance-postgresql")
            .Build();
        await container.StartAsync();
        return container.GetConnectionString();
    }
}
