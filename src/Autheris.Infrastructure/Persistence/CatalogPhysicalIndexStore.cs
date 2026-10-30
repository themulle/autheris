using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Issue #404: reads and replaces the physical (source-observed) non-unique indexes of a table (<c>TABLE_PHYSICAL_INDEXES</c> and
/// <c>TABLE_PHYSICAL_INDEX_COLUMNS</c>). It never touches the operator-declared <c>TABLE_INDEXES</c> (I404-SC-07), stores no index name
/// (I404-SC-01) and uses bound parameters only. Reads are fail closed per index: an index with a column that no longer resolves is dropped whole.
/// </summary>
internal static class CatalogPhysicalIndexStore
{
    internal const string IndexTable = "TABLE_PHYSICAL_INDEXES";
    internal const string ColumnTable = "TABLE_PHYSICAL_INDEX_COLUMNS";

    private const int MaxRowsPerTable = TableKeyDefinitionValidator.MaxPhysicalIndexesPerTable * TableKeyDefinitionValidator.MaxColumnsPerKey;

    /// <summary>One stored index column row.</summary>
    internal readonly record struct Row(int IndexOrdinal, int ColumnOrdinal, string ColumnId, bool Descending);

    private static string SelectSql(KeyDefinitionDialect dialect, string tableFilter) =>
        $@"SELECT i.table_id, i.index_ordinal, ic.column_ordinal, ic.table_column_id, ic.is_descending
           FROM {dialect.Prefix}{IndexTable} i
           INNER JOIN {dialect.Prefix}{ColumnTable} ic ON i.id = ic.index_id
           WHERE {tableFilter}
           ORDER BY i.table_id ASC, i.index_ordinal ASC, ic.column_ordinal ASC";

    /// <summary>Reads the physical indexes of one table.</summary>
    internal static async Task<IReadOnlyList<PhysicalIndexDefinition>> ReadAsync(
        DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid tableId, IReadOnlyList<TableColumn> columns, CancellationToken ct)
    {
        var batch = await ReadBatchAsync(conn, tx, dialect, [tableId], ct).ConfigureAwait(false);
        return batch.TryGetValue(tableId, out var rows) ? Assemble(columns, rows) : Array.Empty<PhysicalIndexDefinition>();
    }

    /// <summary>Reads the raw rows of many tables, one query per chunk of table ids. A table above the row cap keeps only the rows within the cap.</summary>
    internal static async Task<Dictionary<Guid, List<Row>>> ReadBatchAsync(
        DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, IReadOnlyList<Guid> tableIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<Row>>();
        for (var offset = 0; offset < tableIds.Count; offset += CatalogKeyDefinitionStore.BatchChunkSize)
        {
            var count = Math.Min(CatalogKeyDefinitionStore.BatchChunkSize, tableIds.Count - offset);
            var names = new string[count];
            for (var i = 0; i < count; i++)
            {
                names[i] = "@t" + i.ToString(CultureInfo.InvariantCulture);
            }

            await using var cmd = conn.CreateCommand();
            CatalogKeyDefinitionStore.Enlist(cmd, tx);
            cmd.CommandText = SelectSql(dialect, $"i.table_id IN ({string.Join(",", names)})");
            for (var i = 0; i < count; i++)
            {
                CatalogKeyDefinitionStore.AddParameter(cmd, names[i], tableIds[offset + i].ToString());
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out var tid)
                    || !CatalogKeyDefinitionStore.TryReadDescending(reader.GetValue(4), out var descending))
                {
                    continue;
                }

                if (!result.TryGetValue(tid, out var rows))
                {
                    result[tid] = rows = [];
                }

                if (rows.Count < MaxRowsPerTable)
                {
                    rows.Add(new Row(
                        Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                        Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                        reader.GetString(3),
                        descending));
                }
            }
        }

        return result;
    }

    /// <summary>Builds the indexes from raw rows (ordered by index and column ordinal); an index with an unresolved column or an ordinal gap is dropped whole.</summary>
    internal static IReadOnlyList<PhysicalIndexDefinition> Assemble(IReadOnlyList<TableColumn> columns, IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0)
        {
            return Array.Empty<PhysicalIndexDefinition>();
        }

        var nameById = new Dictionary<Guid, string>(columns.Count);
        foreach (var column in columns)
        {
            nameById[column.Id] = column.ColumnName;
        }

        var result = new List<PhysicalIndexDefinition>();
        var start = 0;
        while (start < rows.Count)
        {
            var end = start;
            while (end < rows.Count && rows[end].IndexOrdinal == rows[start].IndexOrdinal)
            {
                end++;
            }

            var names = new List<string>(end - start);
            var directions = new List<KeyColumnDirection>(end - start);
            var valid = true;
            for (var i = start; i < end && valid; i++)
            {
                valid = rows[i].ColumnOrdinal == i - start + 1
                    && Guid.TryParse(rows[i].ColumnId, out var id)
                    && nameById.TryGetValue(id, out var name)
                    && AddColumn(names, directions, name, rows[i].Descending);
            }

            if (valid && names.Count > 0)
            {
                result.Add(new PhysicalIndexDefinition(names, KeyDirections.Canonical(directions) ?? Array.Empty<KeyColumnDirection>()));
            }

            start = end;
        }

        return result;
    }

    private static bool AddColumn(List<string> names, List<KeyColumnDirection> directions, string name, bool descending)
    {
        names.Add(name);
        directions.Add(descending ? KeyColumnDirection.Descending : KeyColumnDirection.Ascending);
        return true;
    }

    /// <summary>
    /// Replaces the physical indexes of the table inside the caller's transaction (DELETE then INSERT, bound parameters, keyed by the
    /// verified table id). Re-validates the caps and the ordinal contract regardless of the caller; a violation throws before anything is written (I404-SC-06).
    /// </summary>
    internal static async Task ReplaceAsync(
        DbConnection conn,
        DbTransaction tx,
        KeyDefinitionDialect dialect,
        Guid tableId,
        IReadOnlyList<PhysicalIndexDefinition> indexes,
        IReadOnlyDictionary<string, Guid> colIdByName,
        CancellationToken ct)
    {
        var resolved = Validate(indexes, colIdByName);
        var tid = tableId.ToString();
        await CatalogKeyDefinitionStore.ExecAsync(conn, tx,
            $"DELETE FROM {dialect.Prefix}{ColumnTable} WHERE index_id IN (SELECT id FROM {dialect.Prefix}{IndexTable} WHERE table_id = @tid)", ct, ("@tid", tid)).ConfigureAwait(false);
        await CatalogKeyDefinitionStore.ExecAsync(conn, tx,
            $"DELETE FROM {dialect.Prefix}{IndexTable} WHERE table_id = @tid", ct, ("@tid", tid)).ConfigureAwait(false);

        for (var n = 0; n < indexes.Count; n++)
        {
            var indexId = Guid.NewGuid().ToString();
            await CatalogKeyDefinitionStore.ExecAsync(conn, tx,
                $"INSERT INTO {dialect.Prefix}{IndexTable} (id, table_id, index_ordinal) VALUES (@id, @tid, @ord)", ct,
                ("@id", indexId), ("@tid", tid), ("@ord", n + 1)).ConfigureAwait(false);
            for (var i = 0; i < resolved[n].Length; i++)
            {
                await CatalogKeyDefinitionStore.ExecAsync(conn, tx,
                    $"INSERT INTO {dialect.Prefix}{ColumnTable} (index_id, column_ordinal, table_column_id, is_descending) VALUES (@idx, @ord, @cid, @desc)", ct,
                    ("@idx", indexId), ("@ord", i + 1), ("@cid", resolved[n][i].ToString()),
                    ("@desc", KeyDirections.IsDescending(indexes[n].Directions, i))).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Replaces the physical indexes of a table looked up by identifier. Returns false when the table does not exist (nothing written).
    /// <paramref name="bumpEpoch"/> runs after the write inside the same transaction (I404-SC-14).
    /// </summary>
    internal static async Task<bool> ReplaceForTableAsync(
        DbConnection conn,
        DbTransaction tx,
        KeyDefinitionDialect dialect,
        TableIdentifier table,
        IReadOnlyList<PhysicalIndexDefinition> indexes,
        Func<Task> bumpEpoch,
        CancellationToken ct)
    {
        Guid tableId;
        await using (var cmd = conn.CreateCommand())
        {
            CatalogKeyDefinitionStore.Enlist(cmd, tx);
            cmd.CommandText = dialect.TableLookupSql;
            CatalogKeyDefinitionStore.AddParameter(cmd, "@domain", table.Domain);
            CatalogKeyDefinitionStore.AddParameter(cmd, "@schema", table.Schema);
            CatalogKeyDefinitionStore.AddParameter(cmd, "@table", table.TableName);
            var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (id is null || !Guid.TryParse(Convert.ToString(id, CultureInfo.InvariantCulture), out tableId))
            {
                return false;
            }
        }

        var colIdByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            CatalogKeyDefinitionStore.Enlist(cmd, tx);
            cmd.CommandText = $"SELECT id, column_name FROM {dialect.Prefix}TABLE_COLUMNS WHERE table_id = @tid";
            CatalogKeyDefinitionStore.AddParameter(cmd, "@tid", tableId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                colIdByName[reader.GetString(1)] = Guid.Parse(reader.GetString(0));
            }
        }

        await ReplaceAsync(conn, tx, dialect, tableId, indexes, colIdByName, ct).ConfigureAwait(false);
        await bumpEpoch().ConfigureAwait(false);
        return true;
    }

    // Caps, ordinal contract (1..n, gap-free by construction of the list), no duplicate column in an index, every column resolves.
    private static Guid[][] Validate(IReadOnlyList<PhysicalIndexDefinition> indexes, IReadOnlyDictionary<string, Guid> colIdByName)
    {
        if (indexes.Count > TableKeyDefinitionValidator.MaxPhysicalIndexesPerTable)
        {
            throw new InvalidOperationException("The physical index list exceeds the per-table cap.");
        }

        var resolved = new Guid[indexes.Count][];
        for (var n = 0; n < indexes.Count; n++)
        {
            var index = indexes[n];
            if (index.Columns.Count is 0 or > TableKeyDefinitionValidator.MaxColumnsPerKey
                || (index.Directions.Count != 0 && index.Directions.Count != index.Columns.Count))
            {
                throw new InvalidOperationException("A physical index has an invalid column count.");
            }

            var ids = new Guid[index.Columns.Count];
            var seen = new HashSet<Guid>();
            for (var i = 0; i < ids.Length; i++)
            {
                if (!colIdByName.TryGetValue(index.Columns[i], out ids[i]) || !seen.Add(ids[i]))
                {
                    throw new InvalidOperationException("A physical index references an unknown or duplicate column.");
                }
            }

            resolved[n] = ids;
        }

        return resolved;
    }
}
