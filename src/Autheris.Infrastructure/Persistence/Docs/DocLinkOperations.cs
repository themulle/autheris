namespace Autheris.Infrastructure.Persistence.Docs;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

/// <summary>Doc-link persistence for <see cref="GovernanceRepository"/>; every statement is scoped by <c>tenant_id</c>.</summary>
internal static class DocLinkOperations
{
    private const string Columns = "tenant_id, link_id, source_kind, app_id, category, doc_id, doc_tenant_id, target_kind, target_domain, target_schema, target_name, target_column, created_by, created_at_utc";
    private const string SourceWhere = "tenant_id = @tenant_id AND source_kind = @source_kind AND app_id = @app_id AND category = @category AND doc_id = @doc_id AND doc_tenant_id = @doc_tenant_id";

    internal const string InsertSql = "INSERT INTO DOC_LINKS (" + Columns + ") VALUES (@tenant_id, @link_id, @source_kind, @app_id, @category, @doc_id, @doc_tenant_id, @target_kind, @target_domain, @target_schema, @target_name, @target_column, @created_by, @created_at_utc)";
    internal const string DeleteSourceSql = "DELETE FROM DOC_LINKS WHERE " + SourceWhere;
    internal const string ListSourceSql = "SELECT " + Columns + " FROM DOC_LINKS WHERE " + SourceWhere + " ORDER BY target_domain, target_schema, target_name, target_column, link_id";
    internal const string DeleteTenantSql = "DELETE FROM DOC_LINKS WHERE tenant_id = @tenant_id";
    internal const string DeleteDeletedOfficialDocSql = "DELETE FROM DOC_LINKS WHERE source_kind = 'Document' AND app_id = @app_id AND category = @category AND doc_id = @doc_id AND doc_tenant_id = ''";
    internal const string DeleteDeletedTenantDocSql = "DELETE FROM DOC_LINKS WHERE tenant_id = @tenant_id AND source_kind = 'Document' AND app_id = @app_id AND category = @category AND doc_id = @doc_id AND doc_tenant_id = @tenant_id";

    public static async Task<IReadOnlyList<DocLinkRecord>> ListForTargetsAsync(GovernanceRepository repo, string tenantId, IReadOnlyList<DocLinkTarget> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var tenant = Tenant(tenantId);
        if (targets.Count == 0) return [];
        if (targets.Count > DocLinkLimits.MaxTargetsPerSource) throw new ArgumentException("Too many link targets.", nameof(targets));
        var sql = new StringBuilder("SELECT ").Append(Columns).Append(" FROM DOC_LINKS WHERE tenant_id = @tenant_id AND (");
        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0) sql.Append(" OR ");
            sql.Append($"(target_domain = @d{i} AND target_schema = @s{i} AND target_name = @n{i})");
        }

        sql.Append(") ORDER BY target_domain, target_schema, target_name, target_column, link_id");
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = repo.CreateDocCommand(conn, null, sql.ToString());
        cmd.AddBound("@tenant_id", tenant);
        for (var i = 0; i < targets.Count; i++)
        {
            cmd.AddBound($"@d{i}", targets[i].Domain);
            cmd.AddBound($"@s{i}", targets[i].Schema);
            cmd.AddBound($"@n{i}", targets[i].Name);
        }

        return await ReadAsync(repo, cmd, "Governance.DocLinks.ListForTargets", ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<DocLinkRecord>> ListForSourceAsync(GovernanceRepository repo, string tenantId, DocLinkSource source, CancellationToken ct)
    {
        var tenant = Tenant(tenantId);
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = repo.CreateDocCommand(conn, null, ListSourceSql);
        BindSource(cmd, tenant, source);
        return await ReadAsync(repo, cmd, "Governance.DocLinks.ListForSource", ct).ConfigureAwait(false);
    }

    public static Task ReplaceForSourceAsync(GovernanceRepository repo, string tenantId, DocLinkSource source, IReadOnlyList<DocLinkTarget> targets, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var tenant = Tenant(tenantId);
        var distinct = targets.Distinct().ToList();
        if (distinct.Count > DocLinkLimits.MaxTargetsPerSource) throw new ArgumentException("Too many link targets.", nameof(targets));
        var now = TimeProvider.System.GetUtcNow();
        return DocumentStoreOperations.RunInTxAsync(repo, async (conn, tx) =>
        {
            await DeleteSourceAsync(repo, conn, tx, tenant, source, ct).ConfigureAwait(false);
            foreach (var t in distinct)
            {
                await using var cmd = repo.CreateDocCommand(conn, tx, InsertSql);
                BindSource(cmd, tenant, source);
                cmd.AddBound("@link_id", Guid.NewGuid(), DbType.Guid);
                cmd.AddBound("@target_kind", t.Kind.ToString());
                cmd.AddBound("@target_domain", t.Domain);
                cmd.AddBound("@target_schema", t.Schema);
                cmd.AddBound("@target_name", t.Name);
                cmd.AddBound("@target_column", t.Column ?? string.Empty);
                cmd.AddBound("@created_by", actor);
                cmd.AddBound("@created_at_utc", now, DbType.DateTimeOffset);
                await repo.ExecuteNonQueryAsync(cmd, "Governance.DocLinks.Insert", ct).ConfigureAwait(false);
            }

            return (true, 0);
        }, ct);
    }

    public static Task<int> DeleteForSourceAsync(GovernanceRepository repo, string tenantId, DocLinkSource source, CancellationToken ct)
    {
        var tenant = Tenant(tenantId);
        return DocumentStoreOperations.RunInTxAsync(repo, async (conn, tx) => (true, await DeleteSourceAsync(repo, conn, tx, tenant, source, ct).ConfigureAwait(false)), ct);
    }

    /// <summary>Removes the links of a just-deleted document in the delete transaction (an official document loses its links in every tenant).</summary>
    public static async Task DeleteForDeletedDocumentAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, DocKey key, CancellationToken ct)
    {
        await using var cmd = repo.CreateDocCommand(conn, tx, key.TenantId is null ? DeleteDeletedOfficialDocSql : DeleteDeletedTenantDocSql);
        if (key.TenantId is not null) cmd.AddBound("@tenant_id", key.TenantId);
        cmd.AddBound("@app_id", key.AppId);
        cmd.AddBound("@category", key.Category.ToString());
        cmd.AddBound("@doc_id", key.DocId);
        await repo.ExecuteNonQueryAsync(cmd, "Governance.DocLinks.DeleteForDocument", ct).ConfigureAwait(false);
    }

    public static async Task DeleteForTenantAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, string tenantId, CancellationToken ct)
    {
        await using var cmd = repo.CreateDocCommand(conn, tx, DeleteTenantSql);
        cmd.AddBound("@tenant_id", tenantId);
        await repo.ExecuteNonQueryAsync(cmd, "Governance.DocLinks.DeleteForTenant", ct).ConfigureAwait(false);
    }

    private static async Task<int> DeleteSourceAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, string tenant, DocLinkSource source, CancellationToken ct)
    {
        await using var cmd = repo.CreateDocCommand(conn, tx, DeleteSourceSql);
        BindSource(cmd, tenant, source);
        return await repo.ExecuteNonQueryAsync(cmd, "Governance.DocLinks.DeleteSource", ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DocLinkRecord>> ReadAsync(GovernanceRepository repo, DbCommand cmd, string id, CancellationToken ct)
    {
        var rows = new List<DocLinkRecord>();
        await using var r = await repo.ExecuteReaderAsync(cmd, id, ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var kind = Enum.Parse<DocLinkSourceKind>(r.GetString(2), ignoreCase: true);
            var category = r.GetString(4);
            var docId = r.GetString(5);
            var docTenant = r.GetString(6);
            var column = r.GetString(11);
            rows.Add(new DocLinkRecord(
                r.GetString(0), r.GetGuid(1),
                new DocLinkSource(kind, r.GetString(3), category.Length == 0 ? null : Enum.Parse<DocCategory>(category, ignoreCase: true), docId.Length == 0 ? null : docId, docTenant.Length == 0 ? null : docTenant),
                new DocLinkTarget(Enum.Parse<DocLinkTargetKind>(r.GetString(7), ignoreCase: true), r.GetString(8), r.GetString(9), r.GetString(10), column.Length == 0 ? null : column),
                r.GetString(12), r.GetFieldValue<DateTimeOffset>(13)));
        }

        return rows;
    }

    private static void BindSource(DbCommand cmd, string tenant, DocLinkSource source)
    {
        cmd.AddBound("@tenant_id", tenant);
        cmd.AddBound("@source_kind", source.Kind.ToString());
        cmd.AddBound("@app_id", source.AppId);
        cmd.AddBound("@category", source.Category?.ToString() ?? string.Empty);
        cmd.AddBound("@doc_id", source.DocId ?? string.Empty);
        cmd.AddBound("@doc_tenant_id", source.DocTenantId ?? string.Empty);
    }

    private static string Tenant(string tenantId) => TenantScope.RequireTenantString(tenantId, GovernanceTable.DocLinks.ToSqlName());
}
