namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

/// <summary>
/// A fresh SQL Server database for the governance contract tests. Uses <c>AUTHERIS_TEST_MSSQL</c> (connection string of a
/// server where the login may create databases) or, without it, a Testcontainers SQL Server (needs Docker).
/// <see cref="IsAvailable"/> is false when neither exists; the tests then return early like the PostgreSQL ones.
/// </summary>
public sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private MsSqlContainer? _container;

    public bool IsAvailable { get; private set; }
    public string ConnectionString { get; private set; } = string.Empty;

    public static async Task<SqlServerTestDatabase> CreateAsync()
    {
        var db = new SqlServerTestDatabase();
        try
        {
            var server = Environment.GetEnvironmentVariable("AUTHERIS_TEST_MSSQL");
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
        catch (Exception)
        {
            db.IsAvailable = false; // no SQL Server / Docker on this machine
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
