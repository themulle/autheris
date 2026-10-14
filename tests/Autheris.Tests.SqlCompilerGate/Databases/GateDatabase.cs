namespace Autheris.Tests.SqlCompilerGate.Databases;

using System.Data.Common;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;

/// <summary>A database loaded with the gate fixture. One instance per dialect; the containers are started once per test class.</summary>
public abstract class GateDatabase : IAsyncLifetime
{
    public abstract GateDialect Dialect { get; }

    /// <summary>The schema that unqualified names resolve to (the catalog's default schema).</summary>
    public abstract string DefaultSchema { get; }

    /// <summary>Why the database is not available (no Docker, image pull failed); null when it is.</summary>
    public string? UnavailableReason { get; protected set; }

    public bool IsAvailable => UnavailableReason is null;

    /// <summary>A new connection (opened), to be disposed by the caller. A shared in-memory database hands out a wrapper over its own connection.</summary>
    public abstract Task<DbConnection> OpenAsync();

    /// <summary>Starts the database. Throws when it cannot start; <see cref="InitializeAsync"/> turns that into <see cref="UnavailableReason"/>.</summary>
    protected abstract Task StartAsync();

    protected abstract Task StopAsync();

    /// <summary>The prefix of table names in the fixture statements (a schema, or empty when the default schema is the user's).</summary>
    protected virtual string? SchemaPrefix => null;

    public async Task InitializeAsync()
    {
        try
        {
            await StartAsync();
            await CreateFixtureAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UnavailableReason = $"{Dialect} database could not start: {ex.GetType().Name}";
        }
    }

    public Task DisposeAsync() => StopAsync();

    private async Task CreateFixtureAsync()
    {
        await using var conn = await OpenAsync();
        foreach (var sql in GateModel.CreateStatements(Dialect, SchemaPrefix)) await ExecAsync(conn, sql);
        await ReloadAsync(conn);
    }

    /// <summary>Restores the fixture rows of <paramref name="tables"/> (all tables when null). Called after a write changed them.</summary>
    public async Task ResetAsync(IReadOnlyCollection<string>? tables = null)
    {
        await using var conn = await OpenAsync();
        await ReloadAsync(conn, tables);
    }

    private async Task ReloadAsync(DbConnection conn, IReadOnlyCollection<string>? tables = null)
    {
        bool Selected(string table) => tables is null || tables.Contains(table, StringComparer.Ordinal);
        foreach (var table in GateModel.Tables.Where(t => Selected(t.Name)))
        {
            await ExecAsync(conn, $"DELETE FROM {SchemaPrefix}{table.Name}");
        }

        foreach (var sql in GateModel.InsertStatements(Dialect, SchemaPrefix, Selected)) await ExecAsync(conn, sql);
    }

    public static async Task<int> ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Every table of the fixture, rows normalized to typed cells (ordered by id).</summary>
    public async Task<Dictionary<string, List<object?[]>>> SnapshotAsync()
    {
        var result = new Dictionary<string, List<object?[]>>(StringComparer.Ordinal);
        await using var conn = await OpenAsync();
        foreach (var table in GateModel.Tables)
        {
            var rows = new List<object?[]>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {string.Join(", ", table.Columns.Select(c => c.Name))} FROM {SchemaPrefix}{table.Name}";
            cmd.CommandTimeout = 30;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            result[table.Name] = rows;
        }

        return result;
    }
}
