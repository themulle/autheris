using System.Globalization;
using Microsoft.Data.Sqlite;

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

    /// <summary>Creates the SQLite data file with 8 rows of the own tenant (masked column filled with the canary) and 3 foreign-tenant rows.</summary>
    public static void Create(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var tx = connection.BeginTransaction();

        Execute(connection, tx, $"CREATE TABLE {TableName} (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL, balance REAL NULL, opened_on TEXT NULL, secret_note TEXT NULL)");

        for (var i = 1; i <= 8; i++)
        {
            Insert(connection, tx, i, OwnTenant, $"Account {i}", i * 10.5, "2024-01-0" + i.ToString(CultureInfo.InvariantCulture), MaskedCanary);
        }

        for (var i = 9; i <= 11; i++)
        {
            Insert(connection, tx, i, ForeignTenant, $"{TenantCanary} {i}", i * 3.0, "2024-02-01", $"{TenantCanary}-note");
        }

        tx.Commit();
    }

    /// <summary>Writes the canary list (one per line) for the checker. The file lives in the data dir and is never uploaded (SEC-SIM-15).</summary>
    public static void WriteCanaries(string path) => File.WriteAllLines(path, Canaries);

    private static void Insert(SqliteConnection connection, SqliteTransaction tx, int id, string tenant, string name, double balance, string opened, string secret)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {TableName} (id, tenant_id, name, balance, opened_on, secret_note) VALUES ($id, $t, $n, $b, $o, $s)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", tenant);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$b", balance);
        cmd.Parameters.AddWithValue("$o", opened);
        cmd.Parameters.AddWithValue("$s", secret);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
