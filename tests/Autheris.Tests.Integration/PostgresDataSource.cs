namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;

/// <summary>
/// A throwaway PostgreSQL database on the shared test server that acts as a governed data source (replaces the removed SQLite
/// data sources of the web-host tests). Tables live in the <c>main</c> schema so catalog metadata and statements that name
/// <c>main</c> keep working; the connection string sets that schema as the search path.
/// </summary>
internal static class PostgresDataSource
{
    public const string Schema = "main";

    /// <summary>Creates an empty database with the <c>main</c> schema, runs <paramref name="script"/> inside it and returns the connection string.</summary>
    public static string Create(string name, string? script = null, string schema = Schema) =>
        Task.Run(() => CreateAsync(name, script, schema)).GetAwaiter().GetResult();

    public static async Task<string> CreateAsync(string name, string? script = null, string schema = Schema)
    {
        var server = await SharedDatabaseServers.PostgreSqlServerAsync().ConfigureAwait(false);
        var safe = new string(name.Where(char.IsLetterOrDigit).ToArray());
        var database = ("autheris_ds_" + safe + "_" + Guid.NewGuid().ToString("N")[..12]).ToLowerInvariant();
        if (database.Length > 63)
        {
            database = database[..63];
        }

        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync().ConfigureAwait(false);
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{database}\"";
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(server)
        {
            Database = database,
            SearchPath = schema,
            MaxPoolSize = 10,
            ConnectionIdleLifetime = 10,
            ConnectionPruningInterval = 5
        }.ConnectionString;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using (var schemaCmd = conn.CreateCommand())
        {
            schemaCmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {schema}";
            await schemaCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(script))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = script;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return connectionString;
    }

    /// <summary>Runs a statement (script) against an existing data-source database.</summary>
    public static void Execute(string connectionString, string sql)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
