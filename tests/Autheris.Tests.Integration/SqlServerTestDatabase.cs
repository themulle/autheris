namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

/// <summary>
/// A fresh SQL Server database for the governance contract tests. Uses <c>AUTHERIS_TEST_MSSQL</c> (connection string of a
/// server where the login may create databases) or, without it, a Testcontainers SQL Server (needs Docker).
/// <see cref="IsAvailable"/> is false when neither exists; the tests then return early like the PostgreSQL ones, unless the run
/// requires its databases (<see cref="ContainerStartup"/>), which fails the startup instead.
/// </summary>
public sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private MsSqlContainer? _container;

    public bool IsAvailable { get; private set; }
    public string ConnectionString { get; private set; } = string.Empty;

    public static Task<SqlServerTestDatabase> CreateAsync() =>
        CreateAsync(Environment.GetEnvironmentVariable("AUTHERIS_TEST_MSSQL"), ContainerStartup.IsRequired());

    /// <param name="server">A server connection string, or null/empty to start a container.</param>
    /// <param name="required">When true (CI), a startup failure throws instead of marking the database unavailable.</param>
    public static async Task<SqlServerTestDatabase> CreateAsync(string? server, bool required)
    {
        var db = new SqlServerTestDatabase();
        try
        {
            if (string.IsNullOrWhiteSpace(server))
            {
                db._container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await db._container.StartAsync();
                server = db._container.GetConnectionString();
            }

            var dbName = "autheris_gov_" + Guid.NewGuid().ToString("N");
            await using (var admin = new SqlConnection(server))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{dbName}]";
                await create.ExecuteNonQueryAsync();
            }

            db.ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = dbName }.ConnectionString;
            db.IsAvailable = true;
        }
        catch (Exception ex)
        {
            db.IsAvailable = false; // no SQL Server / Docker on this machine
            if (db._container != null)
            {
                await db._container.DisposeAsync();
                db._container = null;
            }

            ContainerStartup.FailIfRequired(ex, "The SQL Server test database", required);
        }

        return db;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}
