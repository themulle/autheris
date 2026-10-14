namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

/// <summary>
/// A fresh SQL Server database for the governance contract tests. Uses <c>AUTHERIS_TEST_MSSQL</c> (connection string of a
/// server where the login may create databases) or, without it, the shared Testcontainers SQL Server
/// (<see cref="SharedDatabaseServers"/>, needs Docker).
/// <see cref="IsAvailable"/> is false when neither exists; the tests then return early like the PostgreSQL ones, unless the run
/// requires its databases (<see cref="ContainerStartup"/>), which fails the startup instead.
/// </summary>
public sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private string? _server;
    private string? _name;

    public bool IsAvailable { get; private set; }
    public string ConnectionString { get; private set; } = string.Empty;

    public static Task<SqlServerTestDatabase> CreateAsync() =>
        CreateAsync(Environment.GetEnvironmentVariable("AUTHERIS_TEST_MSSQL"), ContainerStartup.IsRequired());

    /// <param name="server">A server connection string, or null/empty to use the shared container.</param>
    /// <param name="required">When true (CI), a startup failure throws instead of marking the database unavailable.</param>
    public static async Task<SqlServerTestDatabase> CreateAsync(string? server, bool required)
    {
        var db = new SqlServerTestDatabase();
        try
        {
            if (string.IsNullOrWhiteSpace(server))
            {
                server = await SharedDatabaseServers.SqlServerServerAsync();
            }

            db._server = server;
            db._name = "autheris_gov_" + Guid.NewGuid().ToString("N");
            await using (var admin = new SqlConnection(server))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{db._name}]";
                await create.ExecuteNonQueryAsync();
            }

            db.ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = db._name }.ConnectionString;
            db.IsAvailable = true;
        }
        catch (Exception ex)
        {
            db.IsAvailable = false; // no SQL Server / Docker on this machine
            ContainerStartup.FailIfRequired(ex, "The SQL Server test database", required);
        }

        return db;
    }

    /// <summary>Drops the database; an external server (<c>AUTHERIS_TEST_MSSQL</c>) is left clean as well.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            SqlConnection.ClearPool(new SqlConnection(ConnectionString));
            await using var admin = new SqlConnection(_server);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"""
                IF DB_ID(N'{_name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_name}];
                END
                """;
            await drop.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // Best effort: the shared container is removed at process exit anyway.
        }
    }
}
