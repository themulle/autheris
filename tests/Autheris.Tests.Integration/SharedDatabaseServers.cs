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

    /// <summary>TLS material of the shared PostgreSQL server (ssl=on); the CA file is what <c>SSL Mode=VerifyCA</c> clients trust.</summary>
    private static readonly Lazy<TestTlsMaterial> PostgreSqlTls = new(TestTlsMaterial.Create);

    private static readonly Lazy<Task<PostgreSqlContainer>> PostgreSql =
        new(() => StartAsync(new PostgreSqlBuilder("postgres:16-alpine")
            .WithReuse(Reuse)
            .WithLabel("autheris.tests", "shared-postgresql-tls")
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(PostgreSqlTls.Value.ServerCertPem), "/tmp/tls/server.crt")
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(PostgreSqlTls.Value.ServerKeyPem), "/tmp/tls/server.key")
            // The key must belong to the postgres user and be private, so it is copied before the server starts. Every web-host test class gets
            // its own cloned governance database (WP-SIMPL-11), each with its own Npgsql pool, hence max_connections.
            .WithEntrypoint("/bin/sh", "-c",
                "mkdir -p /var/lib/postgresql/tls && cp /tmp/tls/server.crt /tmp/tls/server.key /var/lib/postgresql/tls/ && " +
                "chown -R postgres:postgres /var/lib/postgresql/tls && chmod 600 /var/lib/postgresql/tls/server.key && " +
                "exec docker-entrypoint.sh postgres -c max_connections=500 -c ssl=on " +
                "-c ssl_cert_file=/var/lib/postgresql/tls/server.crt -c ssl_key_file=/var/lib/postgresql/tls/server.key")
            .WithCommand()
            .Build()));

    /// <summary>Path of the CA certificate that signed the shared PostgreSQL server certificate; null when an external server is used.</summary>
    public static string? PostgreSqlCaCertificatePath =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTHERIS_TEST_POSTGRES")) ? PostgreSqlTls.Value.CaFilePath : null;

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
