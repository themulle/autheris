using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Autheris.Tests.Simulation.Corpus;

/// <summary>Raised when corpus SQL tries something the reference sandbox forbids (SEC-SIM-12).</summary>
public sealed class SltReferenceRejectedException(string message) : Exception(message);

/// <summary>
/// The SQLite reference: one temp database file per corpus file, raw <c>Microsoft.Data.Sqlite</c> only. Extension loading is disabled, SQL that
/// names <c>ATTACH</c>, <c>DETACH</c>, <c>load_extension</c>, <c>PRAGMA</c> or <c>VACUUM</c> is rejected before it reaches SQLite, and every command
/// has a timeout and a row cap (SEC-SIM-12). Autheris is never the expected source.
/// </summary>
public sealed partial class SltReference : IAsyncDisposable
{
    public const int CommandTimeoutSeconds = 10;
    public const int MaxRows = 100_000;

    private readonly string _directory;
    private readonly SqliteConnection _connection;

    private SltReference(string directory, string path, SqliteConnection connection)
    {
        _directory = directory;
        Path = path;
        _connection = connection;
    }

    /// <summary>The temp database file. The engine path opens it read-only.</summary>
    public string Path { get; }

    [GeneratedRegex(@"\b(ATTACH|DETACH|load_extension|PRAGMA|VACUUM|readfile|writefile|fts3_tokenizer)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Forbidden();

    public static SltReference Create()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sim-slt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "ref.db");
        return new SltReference(dir, path, OpenConnection(path, readOnly: false));
    }

    /// <summary>Opens a connection with extension loading disabled (shared by the reference and the engine path).</summary>
    public static SqliteConnection OpenConnection(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        connection.EnableExtensions(false);
        return connection;
    }

    /// <summary>Throws <see cref="SltReferenceRejectedException"/> for SQL the sandbox must not run.</summary>
    public static void Guard(string sql)
    {
        if (Forbidden().IsMatch(sql))
        {
            throw new SltReferenceRejectedException("corpus SQL uses a construct the reference sandbox forbids (attach, extension loading, pragma or vacuum).");
        }
    }

    public Task ExecuteAsync(string sql)
    {
        Guard(sql);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<object?[]>> RunAsync(string sql)
    {
        Guard(sql);
        return Task.FromResult(ReadRows(_connection, sql));
    }

    /// <summary>Runs <paramref name="sql"/> on <paramref name="connection"/> with the shared timeout and row cap.</summary>
    public static IReadOnlyList<object?[]> ReadRows(DbConnection connection, string sql, IEnumerable<(string Name, object? Value)>? parameters = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        foreach (var (name, value) in parameters ?? [])
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return Read(cmd);
    }

    /// <summary>
    /// Reads all rows. A wall-clock timer interrupts the SQLite statement after the timeout (the ADO command timeout is
    /// only a busy timeout and cannot stop a long query); an interrupted read throws <see cref="TimeoutException"/>.
    /// </summary>
    public static IReadOnlyList<object?[]> Read(DbCommand cmd, int timeoutSeconds = CommandTimeoutSeconds)
    {
        var rows = new List<object?[]>();
        var interrupted = false;
        using var timer = cmd.Connection is SqliteConnection { Handle: { } handle }
            ? new Timer(_ =>
            {
                interrupted = true;
                SQLitePCL.raw.sqlite3_interrupt(handle);
            }, null, TimeSpan.FromSeconds(timeoutSeconds), Timeout.InfiniteTimeSpan)
            : null;
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (rows.Count >= MaxRows)
                {
                    throw new InvalidOperationException("result exceeds the row cap.");
                }

                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }
        }
        catch (SqliteException) when (interrupted)
        {
            throw new TimeoutException($"statement interrupted after {timeoutSeconds} s.");
        }

        return rows;
    }

    /// <summary>Copies the database (SQLite backup API) into a new reference, for the row-filter scenario's expected result.</summary>
    public SltReference Clone()
    {
        var copy = Create();
        _connection.BackupDatabase(copy._connection);
        return copy;
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // best effort: the OS temp cleaner removes leftovers
        }

        return ValueTask.CompletedTask;
    }
}
