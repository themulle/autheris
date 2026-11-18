namespace Autheris.Infrastructure.Persistence.Docs;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;

internal static class DocumentStoreSql
{
    public const string CheckOfficialActiveSql = "SELECT 1 FROM DOC_OFFICIAL_DOCUMENTS WHERE app_id = @app_id AND category = @category AND doc_id = @doc_id AND deleted_at IS NULL";

    public const string CreateOfficialPgSql = "INSERT INTO DOC_OFFICIAL_DOCUMENTS (app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at) VALUES (@app_id, @category, @doc_id, @title, @content, @content_sha256, @byte_count, @metadata_json, 1, @updated_at, NULL) ON CONFLICT (app_id, category, doc_id) DO UPDATE SET title = EXCLUDED.title, content = EXCLUDED.content, content_sha256 = EXCLUDED.content_sha256, byte_count = EXCLUDED.byte_count, metadata_json = EXCLUDED.metadata_json, version = DOC_OFFICIAL_DOCUMENTS.version + 1, updated_at = EXCLUDED.updated_at, deleted_at = NULL WHERE DOC_OFFICIAL_DOCUMENTS.deleted_at IS NOT NULL RETURNING version";
    public const string CreateOfficialSsSql = "MERGE INTO DOC_OFFICIAL_DOCUMENTS WITH (HOLDLOCK) AS t USING (VALUES (@app_id, @category, @doc_id)) AS s (app_id, category, doc_id) ON t.app_id = s.app_id AND t.category = s.category AND t.doc_id = s.doc_id WHEN MATCHED AND t.deleted_at IS NOT NULL THEN UPDATE SET title = @title, content = @content, content_sha256 = @content_sha256, byte_count = @byte_count, metadata_json = @metadata_json, version = t.version + 1, updated_at = @updated_at, deleted_at = NULL WHEN NOT MATCHED THEN INSERT (app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at) VALUES (@app_id, @category, @doc_id, @title, @content, @content_sha256, @byte_count, @metadata_json, 1, @updated_at, NULL) OUTPUT INSERTED.version;";
    public const string CreateTenantPgSql = "INSERT INTO DOC_TENANT_DOCUMENTS (tenant_id, app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at) VALUES (@tenant_id, @app_id, @category, @doc_id, @title, @content, @content_sha256, @byte_count, @metadata_json, 1, @updated_at, NULL) ON CONFLICT (tenant_id, app_id, category, doc_id) DO UPDATE SET title = EXCLUDED.title, content = EXCLUDED.content, content_sha256 = EXCLUDED.content_sha256, byte_count = EXCLUDED.byte_count, metadata_json = EXCLUDED.metadata_json, version = DOC_TENANT_DOCUMENTS.version + 1, updated_at = EXCLUDED.updated_at, deleted_at = NULL WHERE DOC_TENANT_DOCUMENTS.deleted_at IS NOT NULL RETURNING version";
    public const string CreateTenantSsSql = "MERGE INTO DOC_TENANT_DOCUMENTS WITH (HOLDLOCK) AS t USING (VALUES (@tenant_id, @app_id, @category, @doc_id)) AS s (tenant_id, app_id, category, doc_id) ON t.tenant_id = s.tenant_id AND t.app_id = s.app_id AND t.category = s.category AND t.doc_id = s.doc_id WHEN MATCHED AND t.deleted_at IS NOT NULL THEN UPDATE SET title = @title, content = @content, content_sha256 = @content_sha256, byte_count = @byte_count, metadata_json = @metadata_json, version = t.version + 1, updated_at = @updated_at, deleted_at = NULL WHEN NOT MATCHED THEN INSERT (tenant_id, app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at) VALUES (@tenant_id, @app_id, @category, @doc_id, @title, @content, @content_sha256, @byte_count, @metadata_json, 1, @updated_at, NULL) OUTPUT INSERTED.version;";

    public const string EnsureUsagePgSql = "INSERT INTO DOC_TENANT_USAGE (tenant_id, doc_count, total_bytes, updated_at) VALUES (@tenant_id, 0, 0, @updated_at) ON CONFLICT (tenant_id) DO NOTHING";
    public const string EnsureUsageSsSql = "MERGE INTO DOC_TENANT_USAGE WITH (HOLDLOCK) AS t USING (VALUES (@tenant_id)) AS s (tenant_id) ON t.tenant_id = s.tenant_id WHEN NOT MATCHED THEN INSERT (tenant_id, doc_count, total_bytes, updated_at) VALUES (@tenant_id, 0, 0, @updated_at);";
    public const string ReserveQuotaSql = "UPDATE DOC_TENANT_USAGE SET doc_count = doc_count + @delta_docs, total_bytes = total_bytes + @delta_bytes, updated_at = @updated_at WHERE tenant_id = @tenant_id AND doc_count + @delta_docs <= @max_docs AND total_bytes + @delta_bytes <= @max_bytes";
    public const string DecrementQuotaSql = "UPDATE DOC_TENANT_USAGE SET doc_count = CASE WHEN doc_count > 0 THEN doc_count - 1 ELSE 0 END, total_bytes = CASE WHEN total_bytes >= @old_bytes THEN total_bytes - @old_bytes ELSE 0 END, updated_at = @updated_at WHERE tenant_id = @tenant_id";

    public const string LockOfficialPgSql = "SELECT byte_count, version, deleted_at FROM DOC_OFFICIAL_DOCUMENTS WHERE app_id = @app_id AND category = @category AND doc_id = @doc_id FOR UPDATE";
    public const string LockOfficialSsSql = "SELECT byte_count, version, deleted_at FROM DOC_OFFICIAL_DOCUMENTS WITH (UPDLOCK, ROWLOCK) WHERE app_id = @app_id AND category = @category AND doc_id = @doc_id";
    public const string LockTenantPgSql = "SELECT byte_count, version, deleted_at FROM DOC_TENANT_DOCUMENTS WHERE tenant_id = @tenant_id AND app_id = @app_id AND category = @category AND doc_id = @doc_id FOR UPDATE";
    public const string LockTenantSsSql = "SELECT byte_count, version, deleted_at FROM DOC_TENANT_DOCUMENTS WITH (UPDLOCK, ROWLOCK) WHERE tenant_id = @tenant_id AND app_id = @app_id AND category = @category AND doc_id = @doc_id";

    public const string UpdateOfficialSql = "UPDATE DOC_OFFICIAL_DOCUMENTS SET title = @title, content = @content, content_sha256 = @content_sha256, byte_count = @byte_count, metadata_json = @metadata_json, version = version + 1, updated_at = @updated_at WHERE app_id = @app_id AND category = @category AND doc_id = @doc_id";
    public const string UpdateTenantSql = "UPDATE DOC_TENANT_DOCUMENTS SET title = @title, content = @content, content_sha256 = @content_sha256, byte_count = @byte_count, metadata_json = @metadata_json, version = version + 1, updated_at = @updated_at WHERE tenant_id = @tenant_id AND app_id = @app_id AND category = @category AND doc_id = @doc_id";
    public const string SoftDeleteOfficialSql = "UPDATE DOC_OFFICIAL_DOCUMENTS SET version = version + 1, updated_at = @updated_at, deleted_at = @updated_at WHERE app_id = @app_id AND category = @category AND doc_id = @doc_id";
    public const string SoftDeleteTenantSql = "UPDATE DOC_TENANT_DOCUMENTS SET version = version + 1, updated_at = @updated_at, deleted_at = @updated_at WHERE tenant_id = @tenant_id AND app_id = @app_id AND category = @category AND doc_id = @doc_id";

    public const string PurgeTenantDocsSql = "DELETE FROM DOC_TENANT_DOCUMENTS WHERE tenant_id = @tenant_id";
    public const string PurgeTenantUsageSql = "DELETE FROM DOC_TENANT_USAGE WHERE tenant_id = @tenant_id";
    public const string PurgeOfficialTombstonesSql = "DELETE FROM DOC_OFFICIAL_DOCUMENTS WHERE deleted_at IS NOT NULL AND deleted_at < @cutoff";
    public const string PurgeTenantTombstonesSql = "DELETE FROM DOC_TENANT_DOCUMENTS WHERE deleted_at IS NOT NULL AND deleted_at < @cutoff";
    public const string GetOfficialSql = "SELECT app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at, discovery_hidden FROM DOC_OFFICIAL_DOCUMENTS WHERE app_id = @app_id AND category = @category AND doc_id = @doc_id AND deleted_at IS NULL";
    public const string GetTenantSql = "SELECT app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at, discovery_hidden FROM DOC_TENANT_DOCUMENTS WHERE tenant_id = @tenant_id AND app_id = @app_id AND category = @category AND doc_id = @doc_id AND deleted_at IS NULL";
    public const string SetHiddenOfficialSql = "UPDATE DOC_OFFICIAL_DOCUMENTS SET discovery_hidden = @hidden, updated_at = @updated_at WHERE app_id = @app_id AND category = @category AND doc_id = @doc_id AND deleted_at IS NULL";
    public const string SetHiddenTenantSql = "UPDATE DOC_TENANT_DOCUMENTS SET discovery_hidden = @hidden, updated_at = @updated_at WHERE tenant_id = @tenant_id AND app_id = @app_id AND category = @category AND doc_id = @doc_id AND deleted_at IS NULL";
    public const string GetUsageSql = "SELECT doc_count, total_bytes, updated_at FROM DOC_TENANT_USAGE WHERE tenant_id = @tenant_id";

    public const string ReadAllChangedPgSql = "SELECT CAST(NULL AS VARCHAR(128)) AS tenant_id, app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at, discovery_hidden FROM DOC_OFFICIAL_DOCUMENTS UNION ALL SELECT tenant_id, app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at, discovery_hidden FROM DOC_TENANT_DOCUMENTS ORDER BY updated_at ASC, doc_id ASC";
    public const string ReadAllChangedSsSql = "SELECT CAST(NULL AS NVARCHAR(128)) AS tenant_id, app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at, discovery_hidden FROM DOC_OFFICIAL_DOCUMENTS UNION ALL SELECT tenant_id, app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at, discovery_hidden FROM DOC_TENANT_DOCUMENTS ORDER BY updated_at ASC, doc_id ASC";
    public const string ReadChangedSincePgSql = "SELECT CAST(NULL AS VARCHAR(128)) AS tenant_id, app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at, discovery_hidden FROM DOC_OFFICIAL_DOCUMENTS WHERE updated_at >= @since UNION ALL SELECT tenant_id, app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at, discovery_hidden FROM DOC_TENANT_DOCUMENTS WHERE updated_at >= @since ORDER BY updated_at ASC, doc_id ASC";
    public const string ReadChangedSinceSsSql = "SELECT CAST(NULL AS NVARCHAR(128)) AS tenant_id, app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at, discovery_hidden FROM DOC_OFFICIAL_DOCUMENTS WHERE updated_at >= @since UNION ALL SELECT tenant_id, app_id, category, doc_id, title, content, content_sha256, byte_count, metadata_json, version, updated_at, deleted_at, discovery_hidden FROM DOC_TENANT_DOCUMENTS WHERE updated_at >= @since ORDER BY updated_at ASC, doc_id ASC";

    internal sealed record DocMeta(IReadOnlyList<string>? Tags, string? ErrorCode, string? AuthorSid);

    public static void BindKey(DbCommand cmd, string? tenantId, string appId, DocCategory category, string docId)
    {
        if (tenantId is not null) cmd.AddBound("@tenant_id", tenantId);
        cmd.AddBound("@app_id", appId);
        cmd.AddBound("@category", category.ToString());
        cmd.AddBound("@doc_id", docId);
    }

    public static void BindPayload(DbCommand cmd, DocRecord doc, DateTimeOffset updatedAt)
    {
        BindKey(cmd, doc.TenantId, doc.AppId, doc.Category, doc.DocId);
        cmd.AddBound("@title", doc.Title);
        cmd.AddBound("@content", doc.Content);
        cmd.AddBound("@content_sha256", doc.ContentSha256);
        cmd.AddBound("@byte_count", doc.ByteCount, DbType.Int32);
        var meta = doc.Tags.Count == 0 && doc.ErrorCode is null && doc.AuthorSid is null
            ? null
            : JsonSerializer.Serialize(new DocMeta(doc.Tags, doc.ErrorCode, doc.AuthorSid));
        cmd.AddBound("@metadata_json", (object?)meta ?? DBNull.Value, DbType.String);
        cmd.AddBound("@updated_at", updatedAt.ToUniversalTime(), DbType.DateTimeOffset);
    }

    public static DocRecord ReadDoc(DbDataReader r, string? fixedTenantId, int o)
    {
        var metaJson = r.IsDBNull(o + 7) ? null : r.GetString(o + 7);
        var meta = metaJson is null ? null : JsonSerializer.Deserialize<DocMeta>(metaJson);
        return new DocRecord(
            fixedTenantId, r.GetString(o), Enum.Parse<DocCategory>(r.GetString(o + 1), ignoreCase: true), r.GetString(o + 2),
            r.GetString(o + 3), r.GetString(o + 4), r.GetString(o + 5), r.GetInt32(o + 6),
            meta?.Tags ?? [], meta?.ErrorCode, meta?.AuthorSid,
            r.GetInt64(o + 8), r.GetFieldValue<DateTimeOffset>(o + 9), r.IsDBNull(o + 10) ? null : r.GetFieldValue<DateTimeOffset>(o + 10),
            r.GetBoolean(o + 11));
    }
}
