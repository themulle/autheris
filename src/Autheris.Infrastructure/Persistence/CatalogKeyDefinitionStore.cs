using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Engine specifics of the declared key/index tables (issue #94): qualifier and the existence probe.</summary>
internal sealed record KeyDefinitionDialect(string Prefix, string ExistsSql, bool QualifyExistsName, string TableLookupSql);

/// <summary>
/// Shared read/write of the declared primary key and indexes (issue #94, D-6/D-7, SD-3, SEC-94-07) for all governance engines.
/// Read side: the persisted rows are validated with the full <see cref="TableKeyDefinitionValidator"/>; anything invalid makes
/// the table's metadata unavailable (<see cref="TableMetadataUnavailableException"/>), a partial key is never returned.
/// </summary>
internal static class CatalogKeyDefinitionStore
{
    internal const string PrimaryKeyTable = "TABLE_PRIMARY_KEY_COLUMNS";
    internal const string IndexTable = "TABLE_INDEXES";
    internal const string IndexColumnTable = "TABLE_INDEX_COLUMNS";

    internal static readonly KeyDefinitionDialect Sqlite = new(
        string.Empty,
        "SELECT CASE WHEN EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name) THEN 1 ELSE 0 END",
        false,
        "SELECT id FROM TABLES WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE");

    internal static readonly KeyDefinitionDialect PostgreSql = new(
        string.Empty,
        "SELECT CASE WHEN to_regclass(@name) IS NOT NULL THEN 1 ELSE 0 END",
        false,
        "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) FOR UPDATE");

    internal static readonly KeyDefinitionDialect SqlServer = new(
        "dbo.",
        "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NOT NULL THEN 1 ELSE 0 END",
        true,
        "SELECT id FROM dbo.TABLES WITH (UPDLOCK, ROWLOCK) WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)");

    internal readonly record struct PrimaryKeyRow(int Ordinal, string ColumnId);

    internal readonly record struct IndexRow(string IndexId, string Name, bool IsUnique, int? Ordinal, string? ColumnId);

    private const int MaxIndexRows = TableKeyDefinitionValidator.MaxIndexesPerTable * TableKeyDefinitionValidator.MaxColumnsPerKey;

    // ---------------------------------------------------------------- read

    /// <summary>
    /// Reads and validates the definition of a table. Declared rows win; without declared primary key rows the column Meta markers
    /// (<c>primary_key_ordinal</c>, <c>is_primary_key</c>, <c>role=primary_key</c>) are validated with the same rules and used all-or-nothing.
    /// </summary>
    internal static async Task<(IReadOnlyList<string> PrimaryKey, IReadOnlyList<TableIndexDefinition> Indexes)> ReadAsync(
        DbConnection conn,
        DbTransaction? tx,
        KeyDefinitionDialect dialect,
        TableIdentifier identifier,
        Guid tableId,
        IReadOnlyList<TableColumn> columns,
        CancellationToken ct,
        bool tablesVerified = false)
    {
        if (!tablesVerified && !await TablesExistAsync(conn, tx, dialect, ct).ConfigureAwait(false))
        {
            throw new TableMetadataUnavailableException(identifier);
        }

        var pkRows = new List<PrimaryKeyRow>();
        await using (var cmd = conn.CreateCommand())
        {
            Enlist(cmd, tx);
            cmd.CommandText = $"SELECT key_ordinal, table_column_id FROM {dialect.Prefix}{PrimaryKeyTable} WHERE table_id = @tid ORDER BY key_ordinal ASC";
            AddParameter(cmd, "@tid", tableId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (pkRows.Count >= TableKeyDefinitionValidator.MaxColumnsPerKey)
                {
                    throw new TableMetadataUnavailableException(identifier);
                }

                pkRows.Add(new PrimaryKeyRow(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(1)));
            }
        }

        var indexRows = new List<IndexRow>();
        await using (var cmd = conn.CreateCommand())
        {
            Enlist(cmd, tx);
            cmd.CommandText = $@"SELECT i.id, i.index_name, i.is_unique, ic.column_ordinal, ic.table_column_id
                                 FROM {dialect.Prefix}{IndexTable} i
                                 LEFT JOIN {dialect.Prefix}{IndexColumnTable} ic ON i.id = ic.index_id
                                 WHERE i.table_id = @tid
                                 ORDER BY i.index_name ASC, i.id ASC, ic.column_ordinal ASC";
            AddParameter(cmd, "@tid", tableId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (indexRows.Count >= MaxIndexRows)
                {
                    throw new TableMetadataUnavailableException(identifier);
                }

                indexRows.Add(new IndexRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        return Assemble(identifier, columns, pkRows, indexRows);
    }

    /// <summary>Pure validation of persisted rows; throws <see cref="TableMetadataUnavailableException"/> on any violation.</summary>
    internal static (IReadOnlyList<string> PrimaryKey, IReadOnlyList<TableIndexDefinition> Indexes) Assemble(
        TableIdentifier identifier,
        IReadOnlyList<TableColumn> columns,
        IReadOnlyList<PrimaryKeyRow> pkRows,
        IReadOnlyList<IndexRow> indexRows)
    {
        var nameById = new Dictionary<Guid, string>(columns.Count);
        foreach (var column in columns)
        {
            nameById[column.Id] = column.ColumnName;
        }

        List<string>? primaryKey = null;
        if (pkRows.Count > 0)
        {
            primaryKey = new List<string>(pkRows.Count);
            var expected = 1;
            foreach (var row in pkRows.OrderBy(r => r.Ordinal))
            {
                if (row.Ordinal != expected++ || !TryResolve(nameById, row.ColumnId, out var name))
                {
                    throw new TableMetadataUnavailableException(identifier);
                }

                primaryKey.Add(name);
            }
        }

        var indexes = new List<TableIndexDefinition>();
        var perIndex = new List<(IndexRow First, List<IndexRow> Rows)>();
        foreach (var row in indexRows)
        {
            if (perIndex.Count == 0 || !string.Equals(perIndex[^1].First.IndexId, row.IndexId, StringComparison.Ordinal))
            {
                if (perIndex.Count >= TableKeyDefinitionValidator.MaxIndexesPerTable)
                {
                    throw new TableMetadataUnavailableException(identifier);
                }

                perIndex.Add((row, new List<IndexRow>()));
            }

            perIndex[^1].Rows.Add(row);
        }

        foreach (var (first, rows) in perIndex)
        {
            var names = new List<string>(rows.Count);
            var expected = 1;
            foreach (var row in rows)
            {
                if (row.Ordinal is null || row.ColumnId is null || row.Ordinal != expected++ || !TryResolve(nameById, row.ColumnId, out var name))
                {
                    throw new TableMetadataUnavailableException(identifier);
                }

                names.Add(name);
            }

            indexes.Add(new TableIndexDefinition(first.Name, names, first.IsUnique));
        }

        IReadOnlyList<string> resolvedKey = primaryKey ?? ResolveMarkerPrimaryKey(identifier, columns);
        var candidate = new TableMetadata
        {
            Identifier = identifier,
            Columns = columns,
            PrimaryKeyColumns = resolvedKey,
            Indexes = indexes
        };

        try
        {
            var validated = TableKeyDefinitionValidator.Validate(candidate);
            return (validated.PrimaryKeyColumns, validated.Indexes);
        }
        catch (TableDefinitionException)
        {
            throw new TableMetadataUnavailableException(identifier);
        }
    }

    /// <summary>
    /// H2: validates the column Meta markers as a declaration channel (shared parser <see cref="TableKeyMarkers"/>). Corrupt or
    /// contradicting markers make the table unavailable; no markers means no key. All-or-nothing, never a partial key.
    /// </summary>
    internal static IReadOnlyList<string> ResolveMarkerPrimaryKey(TableIdentifier identifier, IReadOnlyList<TableColumn> columns)
    {
        var result = TableKeyMarkers.Parse(columns);
        return result.IsValid ? result.Columns : throw new TableMetadataUnavailableException(identifier);
    }

    // ---------------------------------------------------------------- write

    /// <summary>Replaces the stored definition of a table inside the caller's transaction. Input must be validated; unknown columns throw.</summary>
    internal static async Task ReplaceAsync(
        DbConnection conn,
        DbTransaction tx,
        KeyDefinitionDialect dialect,
        Guid tableId,
        TableMetadata validated,
        IReadOnlyDictionary<string, Guid> colIdByName,
        CancellationToken ct)
    {
        if (!await TablesExistAsync(conn, tx, dialect, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The governance schema lacks the key/index definition tables.");
        }

        var tid = tableId.ToString();
        await ExecAsync(conn, tx, $"DELETE FROM {dialect.Prefix}{IndexColumnTable} WHERE index_id IN (SELECT id FROM {dialect.Prefix}{IndexTable} WHERE table_id = @tid)", ct, ("@tid", tid)).ConfigureAwait(false);
        await ExecAsync(conn, tx, $"DELETE FROM {dialect.Prefix}{IndexTable} WHERE table_id = @tid", ct, ("@tid", tid)).ConfigureAwait(false);
        await ExecAsync(conn, tx, $"DELETE FROM {dialect.Prefix}{PrimaryKeyTable} WHERE table_id = @tid", ct, ("@tid", tid)).ConfigureAwait(false);

        var ordinal = 1;
        foreach (var column in validated.PrimaryKeyColumns)
        {
            await ExecAsync(conn, tx,
                $"INSERT INTO {dialect.Prefix}{PrimaryKeyTable} (table_id, key_ordinal, table_column_id) VALUES (@tid, @ord, @cid)", ct,
                ("@tid", tid), ("@ord", ordinal++), ("@cid", ColumnId(colIdByName, column))).ConfigureAwait(false);
        }

        foreach (var index in validated.Indexes)
        {
            var indexId = Guid.NewGuid().ToString();
            await ExecAsync(conn, tx,
                $"INSERT INTO {dialect.Prefix}{IndexTable} (id, table_id, index_name, is_unique) VALUES (@id, @tid, @name, @unique)", ct,
                ("@id", indexId), ("@tid", tid), ("@name", index.Name), ("@unique", index.IsUnique)).ConfigureAwait(false);

            for (var i = 0; i < index.Columns.Count; i++)
            {
                await ExecAsync(conn, tx,
                    $"INSERT INTO {dialect.Prefix}{IndexColumnTable} (index_id, column_ordinal, table_column_id) VALUES (@idx, @ord, @cid)", ct,
                    ("@idx", indexId), ("@ord", i + 1), ("@cid", ColumnId(colIdByName, index.Columns[i]))).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Marker upkeep on replace: the legacy markers are rewritten so that a cleared or changed declaration cannot be revived by a stale
    /// marker (H2). Only called when the definition is replaced.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> WithKeyMarkers(
        IReadOnlyDictionary<string, string>? meta,
        string columnName,
        IReadOnlyList<string> primaryKeyColumns)
    {
        var dict = meta != null
            ? new Dictionary<string, string>(meta, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        dict.Remove("primary_key_ordinal");
        dict.Remove("is_primary_key");
        if (dict.TryGetValue("role", out var role) && string.Equals(role, "primary_key", StringComparison.OrdinalIgnoreCase))
        {
            dict.Remove("role");
        }

        for (var i = 0; i < primaryKeyColumns.Count; i++)
        {
            if (string.Equals(primaryKeyColumns[i], columnName, StringComparison.OrdinalIgnoreCase))
            {
                dict["primary_key_ordinal"] = (i + 1).ToString(CultureInfo.InvariantCulture);
                break;
            }
        }

        return dict;
    }

    /// <summary>
    /// SD-5: narrow replacement inside the caller's transaction. Reads the stored columns, validates against them, replaces the three
    /// definition tables and the key markers of the columns only; returns null when the table does not exist. Masking rules are never touched.
    /// </summary>
    internal static async Task<TableKeyDefinitionChange?> ReplaceNarrowAsync(
        DbConnection conn,
        DbTransaction tx,
        KeyDefinitionDialect dialect,
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        Func<Task> bumpEpoch,
        CancellationToken ct)
    {
        Guid tableId;
        await using (var cmd = conn.CreateCommand())
        {
            Enlist(cmd, tx);
            cmd.CommandText = dialect.TableLookupSql;
            AddParameter(cmd, "@domain", table.Domain);
            AddParameter(cmd, "@schema", table.Schema);
            AddParameter(cmd, "@table", table.TableName);
            var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (id is null || !Guid.TryParse(Convert.ToString(id, CultureInfo.InvariantCulture), out tableId))
            {
                return null;
            }
        }

        var columns = new List<TableColumn>();
        await using (var cmd = conn.CreateCommand())
        {
            Enlist(cmd, tx);
            cmd.CommandText = $"SELECT id, column_name, meta_json FROM {dialect.Prefix}TABLE_COLUMNS WHERE table_id = @tid";
            AddParameter(cmd, "@tid", tableId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                columns.Add(new TableColumn
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    TableId = tableId,
                    ColumnName = reader.GetString(1),
                    Meta = SqliteGovernanceRepository.ParseColumnMetaJson(reader.IsDBNull(2) ? null : reader.GetString(2))
                });
            }
        }

        var before = new TableKeyDefinition(Array.Empty<string>(), Array.Empty<TableIndexDefinition>());
        var beforeUnavailable = false;
        try
        {
            var (pk, idx) = await ReadAsync(conn, tx, dialect, table, tableId, columns, ct).ConfigureAwait(false);
            before = new TableKeyDefinition(pk, idx);
        }
        catch (TableMetadataUnavailableException)
        {
            // An invalid stored definition is exactly what an administrator replaces here; the audit "before" is then flagged, no partial data.
            beforeUnavailable = true;
        }

        var validated = TableKeyDefinitionValidator.Validate(new TableMetadata
        {
            Identifier = table,
            Columns = columns,
            PrimaryKeyColumns = primaryKey,
            Indexes = indexes
        });

        var colIdByName = new Dictionary<string, Guid>(columns.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            colIdByName[column.ColumnName] = column.Id;
        }

        await ReplaceAsync(conn, tx, dialect, tableId, validated, colIdByName, ct).ConfigureAwait(false);

        foreach (var column in columns)
        {
            var markers = WithKeyMarkers(column.Meta, column.ColumnName, validated.PrimaryKeyColumns);
            if (markers.Count == column.Meta.Count && markers.All(kv => column.Meta.TryGetValue(kv.Key, out var v) && v == kv.Value))
            {
                continue;
            }

            await ExecAsync(conn, tx,
                $"UPDATE {dialect.Prefix}TABLE_COLUMNS SET meta_json = @meta WHERE id = @id", ct,
                ("@meta", markers.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(markers) : "{}"),
                ("@id", column.Id.ToString())).ConfigureAwait(false);
        }

        await bumpEpoch().ConfigureAwait(false);
        return new TableKeyDefinitionChange(before, new TableKeyDefinition(validated.PrimaryKeyColumns, validated.Indexes), beforeUnavailable);
    }

    // ---------------------------------------------------------------- helpers

    internal static async Task<bool> TablesExistAsync(DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, CancellationToken ct)
    {
        foreach (var table in new[] { PrimaryKeyTable, IndexTable, IndexColumnTable })
        {
            await using var cmd = conn.CreateCommand();
            Enlist(cmd, tx);
            cmd.CommandText = dialect.ExistsSql;
            AddParameter(cmd, "@name", dialect.QualifyExistsName ? dialect.Prefix + table : table);
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (result is null || Convert.ToInt32(result, CultureInfo.InvariantCulture) != 1)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolve(Dictionary<Guid, string> nameById, string columnId, out string name)
    {
        name = string.Empty;
        return Guid.TryParse(columnId, out var id) && nameById.TryGetValue(id, out name!);
    }

    private static string ColumnId(IReadOnlyDictionary<string, Guid> colIdByName, string column) =>
        colIdByName.TryGetValue(column, out var id)
            ? id.ToString()
            : throw new TableDefinitionException("The key or index definition references a column that does not exist.");

    // Microsoft.Data.Sqlite treats an explicitly assigned null as "no transaction" even inside a snapshot scope: assign only when set.
    private static void Enlist(DbCommand cmd, DbTransaction? tx)
    {
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    private static async Task ExecAsync(DbConnection conn, DbTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        Enlist(cmd, tx);
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            AddParameter(cmd, name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
