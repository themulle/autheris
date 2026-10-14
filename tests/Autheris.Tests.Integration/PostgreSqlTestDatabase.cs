namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Npgsql;

/// <summary>
/// A fresh PostgreSQL database on the shared test server (<see cref="SharedDatabaseServers"/>). Tables, schemas, extensions,
/// collations and advisory locks are database-scoped, so each consumer is isolated as with its own container.
/// <see cref="IsAvailable"/> is false when Docker is missing; the tests then return early, unless the run requires its databases
/// (<see cref="ContainerStartup"/>), which fails the startup instead.
/// </summary>
public sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private string? _server;
    private string? _name;

    public bool IsAvailable { get; private set; }
    public string ConnectionString { get; private set; } = string.Empty;

    public static async Task<PostgreSqlTestDatabase> CreateAsync()
    {
        var db = new PostgreSqlTestDatabase();
        try
        {
            db._server = await SharedDatabaseServers.PostgreSqlServerAsync();
            db._name = "autheris_" + Guid.NewGuid().ToString("N");
            await using (var admin = new NpgsqlConnection(db._server))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{db._name}\"";
                await create.ExecuteNonQueryAsync();
            }

            db.ConnectionString = new NpgsqlConnectionStringBuilder(db._server) { Database = db._name }.ConnectionString;
            db.IsAvailable = true;
        }
        catch (Exception ex)
        {
            db.IsAvailable = false; // no Docker on this machine
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test database");
        }

        return db;
    }

    /// <summary>Drops the database so idle pooled connections do not exhaust the shared server's connection limit.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
            await using var admin = new NpgsqlConnection(_server);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // Best effort: the shared container is removed at process exit anyway.
        }
    }
}
