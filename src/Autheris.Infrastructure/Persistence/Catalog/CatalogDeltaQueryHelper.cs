namespace Autheris.Infrastructure.Persistence.Catalog;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Chunked batch helpers for delta catalog queries and deferred <c>POLICY_EPOCHS.change_version</c> reconciliation.
/// Lives under <c>Persistence/Catalog</c> with parameterized tuple predicates bounded by DB parameter limits.
/// </summary>
internal static class CatalogDeltaQueryHelper
{
    internal const int MaxIdentifiersPerChunk = 500;

    internal static async Task<IReadOnlyList<TableIdentifier>> GetTablesChangedSinceAsync(
        GovernanceRepository repo,
        long sinceVersion,
        CancellationToken ct)
    {
        var changed = new List<TableIdentifier>();
        var seen = new HashSet<TableIdentifier>();

        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = GovernanceRepository.CatalogSql.SelectChangedTablesSinceSql;
        cmd.AddBound("@sinceVersion", sinceVersion, DbType.Int64);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var identifier = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (seen.Add(identifier))
                {
                    changed.Add(identifier);
                }
            }
            catch (ArgumentException)
            {
                repo.LogCatalogNotice("A changed table entry in POLICY_EPOCHS has an invalid identifier and was skipped.");
            }
        }

        return changed;
    }

    internal static async Task<IReadOnlyList<TableMetadata>> GetTablesAsync(
        GovernanceRepository repo,
        IReadOnlyCollection<TableIdentifier> identifiers,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        if (identifiers.Count == 0)
        {
            return Array.Empty<TableMetadata>();
        }

        var unique = new List<TableIdentifier>(identifiers.Count);
        var seen = new HashSet<TableIdentifier>();
        foreach (var id in identifiers)
        {
            if (seen.Add(id))
            {
                unique.Add(id);
            }
        }

        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);

        var activeTables = new List<(Guid Id, TableIdentifier Identifier, Table Table)>(unique.Count);
        for (var offset = 0; offset < unique.Count; offset += MaxIdentifiersPerChunk)
        {
            var count = Math.Min(MaxIdentifiersPerChunk, unique.Count - offset);
            await using var cmd = conn.CreateCommand();
            var sb = new StringBuilder(GovernanceRepository.CatalogSql.SelectTablesWherePrefix.Length + count * 96);
            sb.Append(GovernanceRepository.CatalogSql.SelectTablesWherePrefix);
            sb.Append("is_active = 1 AND (");
            AppendTableIdentifierTupleOrClause(cmd, sb, "source_name", unique, offset, count);
            sb.Append(')');
            cmd.CommandText = sb.ToString();

            var chunkTables = await repo.LoadActiveTablesAsync(cmd, ct).ConfigureAwait(false);
            activeTables.AddRange(chunkTables);
        }

        if (activeTables.Count == 0)
        {
            return Array.Empty<TableMetadata>();
        }

        var columnsByTable = new Dictionary<Guid, List<TableColumn>>();
        var maskingRulesByTable = new Dictionary<Guid, Dictionary<string, MaskingRule>>();
        var corruptedTableIds = new HashSet<Guid>();

        for (var offset = 0; offset < activeTables.Count; offset += MaxIdentifiersPerChunk)
        {
            var count = Math.Min(MaxIdentifiersPerChunk, activeTables.Count - offset);
            var idStrings = new string[count];
            for (var i = 0; i < count; i++)
            {
                idStrings[i] = activeTables[offset + i].Id.ToString();
            }

            await using var cmd = conn.CreateCommand();
            var prefix = repo.IsPostgreSql
                ? GovernanceRepository.CatalogSql.SelectColumnsByTableIdsPrefixPg
                : GovernanceRepository.CatalogSql.SelectColumnsByTableIdsPrefixSs;
            cmd.CommandText = prefix + ConsentSqlCore.BindIdInClause(cmd, "tid", idStrings) + ") ORDER BY c.table_id, c.ordinal, c.column_name";

            var (chunkCols, chunkMasks, chunkCorrupt) = await repo.LoadBatchTableColumnsAsync(cmd, ct).ConfigureAwait(false);
            foreach (var kv in chunkCols)
            {
                columnsByTable[kv.Key] = kv.Value;
            }

            foreach (var kv in chunkMasks)
            {
                maskingRulesByTable[kv.Key] = kv.Value;
            }

            corruptedTableIds.UnionWith(chunkCorrupt);
        }

        var result = await repo.AssembleTableMetadataListAsync(
            conn,
            activeTables,
            columnsByTable,
            maskingRulesByTable,
            corruptedTableIds,
            epochs: null,
            tryGetCached: null,
            ct).ConfigureAwait(false);

        return await repo.ApplyDatasetCatalogOverlayAsync(result, ct).ConfigureAwait(false);
    }

    internal static async Task StampDeferredChangeVersionsAsync(
        DbConnection conn,
        DbTransaction tx,
        IReadOnlyList<(TableIdentifier Table, long Seq)> tables,
        long changeVersion,
        CancellationToken ct)
    {
        if (tables.Count == 0)
        {
            return;
        }

        var now = TimeProvider.System.GetUtcNow().ToString("O");
        for (var offset = 0; offset < tables.Count; offset += MaxIdentifiersPerChunk)
        {
            var count = Math.Min(MaxIdentifiersPerChunk, tables.Count - offset);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var sb = new StringBuilder();
            sb.Append("UPDATE POLICY_EPOCHS SET change_version = @changeVersion, updated_at = @now WHERE ");
            cmd.AddBound("@changeVersion", changeVersion, DbType.Int64);
            cmd.AddBound("@now", now);

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(" OR ");
                }

                var dn = "@d" + i.ToString(CultureInfo.InvariantCulture);
                var sn = "@s" + i.ToString(CultureInfo.InvariantCulture);
                var tn = "@t" + i.ToString(CultureInfo.InvariantCulture);
                sb.Append("(LOWER(domain) = LOWER(").Append(dn)
                  .Append(") AND LOWER(schema_name) = LOWER(").Append(sn)
                  .Append(") AND LOWER(table_name) = LOWER(").Append(tn).Append("))");
                cmd.AddBound(dn, tables[offset + i].Table.Domain);
                cmd.AddBound(sn, tables[offset + i].Table.Schema);
                cmd.AddBound(tn, tables[offset + i].Table.TableName);
            }

            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static void AppendTableIdentifierTupleOrClause(
        DbCommand cmd,
        StringBuilder sb,
        string sourceColumnName,
        IReadOnlyList<TableIdentifier> identifiers,
        int offset,
        int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(" OR ");
            }

            var dn = "@d" + i.ToString(CultureInfo.InvariantCulture);
            var sn = "@s" + i.ToString(CultureInfo.InvariantCulture);
            var tn = "@t" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append("(LOWER(").Append(sourceColumnName).Append(") = LOWER(").Append(dn)
              .Append(") AND LOWER(schema_name) = LOWER(").Append(sn)
              .Append(") AND LOWER(table_name) = LOWER(").Append(tn).Append("))");
            cmd.AddBound(dn, identifiers[offset + i].Domain);
            cmd.AddBound(sn, identifiers[offset + i].Schema);
            cmd.AddBound(tn, identifiers[offset + i].TableName);
        }
    }
}
