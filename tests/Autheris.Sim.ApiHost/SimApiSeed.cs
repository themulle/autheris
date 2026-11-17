using Npgsql;

namespace Autheris.Sim.ApiHost;

/// <summary>
/// Synthetic throwaway fixture with planted canaries (SEC-SIM-04/14). The masked column value and the forbidden-tenant marker must never
/// appear in any response; the checker fails the run when they do. Nothing here is real data.
/// </summary>
public static class SimApiSeed
{
    public const string TableName = "sim_accounts";

    public const string OwnTenant = "tenant_sim";

    public const string ForeignTenant = "tenant_forbidden";

    public const string MaskedCanary = "SIMCANARY-MASK-7f3a91";

    public const string TenantCanary = "SIMCANARY-TENANT-9b1c42";

    public static IReadOnlyList<string> Canaries { get; } = [MaskedCanary, TenantCanary];

    public const string DataDatabase = "sim_data";

    /// <summary>
    /// Creates the throwaway PostgreSQL data database next to the governance database (the SQLite data source was removed): schema
    /// <c>main</c> keeps the route names stable. 8 rows of the own tenant (masked column filled with the canary) and 3 foreign-tenant rows.
    /// Returns the connection string of the data database.
    /// </summary>
    public static async Task<string> CreateAsync(string adminConnectionString)
    {
        var admin = new NpgsqlConnectionStringBuilder(adminConnectionString) { Pooling = false };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {DataDatabase} WITH (FORCE)").ConfigureAwait(false);
            await ExecuteAsync(connection, $"CREATE DATABASE {DataDatabase}").ConfigureAwait(false);
        }

        var data = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = DataDatabase, Pooling = false };
        await using var dataConnection = new NpgsqlConnection(data.ConnectionString);
        await dataConnection.OpenAsync().ConfigureAwait(false);
        await using var tx = await dataConnection.BeginTransactionAsync().ConfigureAwait(false);

        await ExecuteAsync(dataConnection, "CREATE SCHEMA main", tx).ConfigureAwait(false);
        await ExecuteAsync(dataConnection, $"CREATE TABLE main.{TableName} (id integer PRIMARY KEY, tenant_id varchar(64) NOT NULL, name varchar(256) NOT NULL, balance numeric(18,2) NULL, opened_on date NULL, secret_note varchar(256) NULL)", tx).ConfigureAwait(false);

        for (var i = 1; i <= 8; i++)
        {
            await InsertAsync(dataConnection, tx, i, OwnTenant, $"Account {i}", i * 10.5m, new DateOnly(2024, 1, i), MaskedCanary).ConfigureAwait(false);
        }

        for (var i = 9; i <= 11; i++)
        {
            await InsertAsync(dataConnection, tx, i, ForeignTenant, $"{TenantCanary} {i}", i * 3.0m, new DateOnly(2024, 2, 1), $"{TenantCanary}-note").ConfigureAwait(false);
        }

        await tx.CommitAsync().ConfigureAwait(false);
        return data.ConnectionString;
    }

    /// <summary>Best-effort removal of the data database on shutdown.</summary>
    public static async Task DropAsync(string adminConnectionString)
    {
        try
        {
            var admin = new NpgsqlConnectionStringBuilder(adminConnectionString) { Pooling = false };
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {DataDatabase} WITH (FORCE)").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // The database server of the job is removed with the job.
        }
    }

    /// <summary>Writes the canary list (one per line) for the checker. The file lives in the data dir and is never uploaded (SEC-SIM-15).</summary>
    public static void WriteCanaries(string path) => File.WriteAllLines(path, Canaries);

    private static async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction tx, int id, string tenant, string name, decimal balance, DateOnly opened, string secret)
    {
        await using var cmd = new NpgsqlCommand($"INSERT INTO main.{TableName} (id, tenant_id, name, balance, opened_on, secret_note) VALUES ($1, $2, $3, $4, $5, $6)", connection, tx);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenant);
        cmd.Parameters.AddWithValue(name);
        cmd.Parameters.AddWithValue(balance);
        cmd.Parameters.AddWithValue(opened);
        cmd.Parameters.AddWithValue(secret);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
