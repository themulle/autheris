namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>One change read from CHANGETABLE joined with the current row (null <see cref="Row"/> for a delete or a vanished row).</summary>
public sealed record CdcSourceRow(
    long Version,
    CdcOperation Operation,
    IReadOnlyList<object?> KeyValues,
    IReadOnlyDictionary<string, object?> Key,
    IReadOnlyDictionary<string, object?>? Row);

/// <summary>
/// The only place that sends text to a tracked source database (fixed statements over validated identifiers, bound values; a login with
/// <c>SELECT</c> and <c>VIEW CHANGE TRACKING</c> suffices). It never reads a catalog view: keys come from the declared table definition
/// (F-GOV-21, PLAN-CDC-99 D-A3).
/// </summary>
internal static class CdcSourceQueries
{
    /// <summary>SQL Server error: snapshot isolation is not enabled for the database.</summary>
    public const int SnapshotIsolationDisabledError = 3952;

    private static string Qualified(TableIdentifier table) => $"[{table.Schema}].[{table.TableName}]";

    private static DbParameter Param(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
        return p;
    }

    private static async Task<long?> ScalarLongAsync(DbConnection conn, DbTransaction? tx, string sql, string? tableParam, int timeoutSeconds, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.CommandTimeout = timeoutSeconds;
        if (tableParam is not null)
        {
            Param(cmd, "@fullName", tableParam);
        }

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result != null && result != DBNull.Value && long.TryParse(Convert.ToString(result, CultureInfo.InvariantCulture), out var v) ? v : null;
    }

    /// <summary><c>CHANGE_TRACKING_MIN_VALID_VERSION</c> of the table; null when tracking is off (or not visible to the login).</summary>
    public static Task<long?> GetMinValidVersionAsync(DbConnection conn, DbTransaction? tx, TableIdentifier table, int timeoutSeconds, CancellationToken ct) =>
        ScalarLongAsync(conn, tx, "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@fullName));", Qualified(table), timeoutSeconds, ct);

    public static Task<long?> GetCurrentVersionAsync(DbConnection conn, DbTransaction? tx, int timeoutSeconds, CancellationToken ct) =>
        ScalarLongAsync(conn, tx, "SELECT CHANGE_TRACKING_CURRENT_VERSION();", null, timeoutSeconds, ct);

    /// <summary>The key columns of the change table, in result order (every column except the <c>SYS_CHANGE_*</c> ones).</summary>
    public static async Task<IReadOnlyList<string>> ReadChangeTableKeyColumnsAsync(
        DbConnection conn, TableIdentifier table, long minValidVersion, int timeoutSeconds, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT TOP (0) * FROM CHANGETABLE(CHANGES {Qualified(table)}, @minValid) AS CT;";
        cmd.CommandTimeout = timeoutSeconds;
        Param(cmd, "@minValid", minValidVersion);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var columns = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (!name.StartsWith("SYS_CHANGE_", StringComparison.OrdinalIgnoreCase))
            {
                columns.Add(name);
            }
        }

        return columns;
    }

    /// <summary>
    /// Opens and commits a SNAPSHOT transaction that reads nothing. Returns false when the database has snapshot isolation off
    /// (error 3952); other failures propagate as transient.
    /// </summary>
    public static async Task<bool> ProbeSnapshotIsolationAsync(DbConnection conn, TableIdentifier table, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Snapshot, ct).ConfigureAwait(false);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT TOP (0) 1 FROM {Qualified(table)};";
                cmd.Transaction = tx;
                cmd.CommandTimeout = timeoutSeconds;
                await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbException ex) when (ErrorNumber(ex) == SnapshotIsolationDisabledError)
        {
            return false;
        }
    }

    /// <summary>The provider error number (<c>SqlException.Number</c>) of a database exception, or 0.</summary>
    public static int ErrorNumber(DbException ex) =>
        ex.GetType().GetProperty("Number")?.GetValue(ex) is int n ? n : 0;

    /// <summary>
    /// Reads one batch with an explicit, validated column list (never <c>T.*</c>). <paramref name="exactVersion"/> reads one change
    /// version completely (no TOP).
    /// </summary>
    public static async Task<List<CdcSourceRow>> ReadChangesAsync(
        DbConnection conn,
        DbTransaction? tx,
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<string> columns,
        long lastVersion,
        int batchSize,
        long? exactVersion,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var join = string.Join(" AND ", primaryKey.Select(pk => $"T.[{pk}] = CT.[{pk}]"));
        var top = exactVersion is null ? "TOP (@batchSize) " : string.Empty;
        var versionFilter = exactVersion is null ? string.Empty : "WHERE CT.SYS_CHANGE_VERSION = @version ";
        var keySelect = string.Join(", ", primaryKey.Select(pk => $"CT.[{pk}]"));
        var rowSelect = columns.Count == 0 ? string.Empty : ", " + string.Join(", ", columns.Select(c => $"T.[{c}]"));
        var order = string.Join(", ", primaryKey.Select(pk => $"CT.[{pk}]"));
        var sql = $"SELECT {top}CT.SYS_CHANGE_VERSION, CT.SYS_CHANGE_OPERATION, {keySelect}, " +
                  $"CASE WHEN T.[{primaryKey[0]}] IS NULL THEN 0 ELSE 1 END{rowSelect} " +
                  $"FROM CHANGETABLE(CHANGES {Qualified(table)}, @lastVersion) AS CT " +
                  $"LEFT OUTER JOIN {Qualified(table)} AS T ON {join} {versionFilter}" +
                  $"ORDER BY CT.SYS_CHANGE_VERSION ASC, {order};";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.CommandTimeout = timeoutSeconds;
        Param(cmd, "@lastVersion", lastVersion);
        if (exactVersion is null)
        {
            Param(cmd, "@batchSize", batchSize);
        }
        else
        {
            Param(cmd, "@version", exactVersion.Value);
        }

        var rows = new List<CdcSourceRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var presentOrdinal = 2 + primaryKey.Count;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var version = reader.GetInt64(0);
            var op = (reader.IsDBNull(1) ? "U" : reader.GetString(1)).Trim().ToUpperInvariant() switch
            {
                "I" => CdcOperation.Insert,
                "D" => CdcOperation.Delete,
                _ => CdcOperation.Update
            };

            var keyValues = new object?[primaryKey.Count];
            var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < primaryKey.Count; i++)
            {
                keyValues[i] = reader.IsDBNull(2 + i) ? null : reader.GetValue(2 + i);
                key[primaryKey[i]] = keyValues[i];
            }

            Dictionary<string, object?>? row = null;
            if (op != CdcOperation.Delete && Convert.ToInt32(reader.GetValue(presentOrdinal), CultureInfo.InvariantCulture) == 1)
            {
                row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns.Count; i++)
                {
                    row[columns[i]] = reader.IsDBNull(presentOrdinal + 1 + i) ? null : reader.GetValue(presentOrdinal + 1 + i);
                }
            }

            rows.Add(new CdcSourceRow(version, row is null ? CdcOperation.Delete : op, keyValues, key, row));
        }

        return rows;
    }
}
