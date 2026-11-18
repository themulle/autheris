namespace Autheris.Infrastructure.Persistence.GoldenQueries;

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence.Dialects;

internal static class GoldenQueryStoreOperations
{
    private const string GoldenDeleteSql = "DELETE FROM GOLDEN_QUERIES WHERE tenant_id = @tenant_id AND table_id = @table_id AND source = @source";

    private const string GoldenInsertSql = "INSERT INTO GOLDEN_QUERIES (id, tenant_id, table_id, source, source_key, title, description, query_text, variables_json, content_hash, updated_at) VALUES (@id, @tenant_id, @table_id, @source, @source_key, @title, @description, @query_text, @variables_json, @content_hash, @updated_at)";

    private const string GoldenSelectSql = "SELECT g.id, g.table_id, t.source_name, t.schema_name, t.table_name, g.source, g.source_key, g.title, g.description, g.query_text, g.variables_json, g.content_hash, g.updated_at FROM GOLDEN_QUERIES g INNER JOIN TABLES t ON t.id = g.table_id WHERE g.tenant_id = @tenant_id AND t.is_active = 1 ORDER BY g.table_id, g.source, g.source_key";

    public static async Task ReplaceForTableAsync(
        GovernanceRepository repo,
        TenantId tenant,
        string tableId,
        GoldenQuerySource source,
        IReadOnlyList<GoldenQueryStoreEntry> entries,
        CancellationToken ct)
    {
        var validTenant = TenantScope.RequireTenantString(tenant.Value, GovernanceTable.GoldenQueries.ToSqlName());
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);
        ArgumentNullException.ThrowIfNull(entries);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.SourceKey))
            {
                throw new ArgumentException("Golden query source keys must be unique per table and source.", nameof(entries));
            }
        }

        var sourceCode = ToSourceCode(source);
        var now = TimeProvider.System.GetUtcNow().ToUniversalTime();
        CatalogVersion newVersion;
        await using (var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var del = repo.CreateGoldenCommand(conn, tx, GoldenDeleteSql))
                {
                    del.AddBound("@tenant_id", validTenant);
                    del.AddBound("@table_id", tableId);
                    del.AddBound("@source", sourceCode);
                    await repo.ExecuteNonQueryAsync(del, "GoldenQueries.Delete", ct).ConfigureAwait(false);
                }

                foreach (var entry in entries)
                {
                    await using var ins = repo.CreateGoldenCommand(conn, tx, GoldenInsertSql);
                    ins.AddBound("@id", Guid.NewGuid().ToString("D"));
                    ins.AddBound("@tenant_id", validTenant);
                    ins.AddBound("@table_id", tableId);
                    ins.AddBound("@source", sourceCode);
                    ins.AddBound("@source_key", entry.SourceKey);
                    ins.AddBound("@title", entry.Title);
                    ins.AddBound("@description", entry.Description, DbType.String);
                    ins.AddBound("@query_text", entry.QueryText);
                    ins.AddBound("@variables_json", entry.VariablesJson, DbType.String);
                    ins.AddBound("@content_hash", entry.ComputeContentHash());
                    ins.AddBound("@updated_at", now, DbType.DateTimeOffset);
                    await repo.ExecuteNonQueryAsync(ins, "GoldenQueries.Insert", ct).ConfigureAwait(false);
                }

                newVersion = await repo.BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
                await repo.CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            }
            catch
            {
                await repo.RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                throw;
            }
        }

        await repo.PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
        await repo.FlushGoldenScopeAsync(ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<GoldenQueryStoreRecord>> GetForTenantAsync(GovernanceRepository repo, TenantId tenant, CancellationToken ct)
    {
        var validTenant = TenantScope.RequireTenantString(tenant.Value, GovernanceTable.GoldenQueries.ToSqlName());
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = repo.CreateGoldenCommand(conn, null, GoldenSelectSql);
        cmd.AddBound("@tenant_id", validTenant);

        var list = new List<GoldenQueryStoreRecord>();
        await using var reader = await repo.ExecuteReaderAsync(cmd, "GoldenQueries.GetForTenant", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new GoldenQueryStoreRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                FromSourceCode(reader.GetString(5)),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11).Trim(),
                reader.GetFieldValue<DateTimeOffset>(12)));
        }

        return list;
    }

    private static string ToSourceCode(GoldenQuerySource source) => source switch
    {
        GoldenQuerySource.Dbt => "DBT",
        GoldenQuerySource.Config => "CONFIG",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static GoldenQuerySource FromSourceCode(string code) => code switch
    {
        "DBT" => GoldenQuerySource.Dbt,
        "CONFIG" => GoldenQuerySource.Config,
        _ => throw new InvalidOperationException("Unknown golden query source in the governance store.")
    };
}
