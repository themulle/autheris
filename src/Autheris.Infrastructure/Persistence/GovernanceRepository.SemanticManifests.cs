namespace Autheris.Infrastructure.Persistence;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository : ISemanticManifestStore
{
    private const string SelectPg = "SELECT manifest_version, content_hash, format_version, models_count, metrics_count, manifest_json, imported_by, imported_at FROM SEMANTIC_MANIFESTS WHERE tenant_id = @tenant;";
    private const string SelectSs = "SELECT manifest_version, content_hash, format_version, models_count, metrics_count, manifest_json, imported_by, imported_at FROM dbo.SEMANTIC_MANIFESTS WHERE tenant_id = @tenant;";

    private const string UpsertPg = @"
            INSERT INTO SEMANTIC_MANIFESTS (tenant_id, manifest_version, content_hash, format_version, models_count, metrics_count, manifest_json, imported_by, imported_at)
            VALUES (@tenant, @version, @hash, @format, @models, @metrics, @json, @by, @at)
            ON CONFLICT (tenant_id) DO UPDATE SET
                manifest_version = EXCLUDED.manifest_version,
                content_hash = EXCLUDED.content_hash,
                format_version = EXCLUDED.format_version,
                models_count = EXCLUDED.models_count,
                metrics_count = EXCLUDED.metrics_count,
                manifest_json = EXCLUDED.manifest_json,
                imported_by = EXCLUDED.imported_by,
                imported_at = EXCLUDED.imported_at;";

    private const string UpsertSs = @"
            MERGE dbo.SEMANTIC_MANIFESTS WITH (HOLDLOCK) AS t
            USING (SELECT @tenant AS tenant_id) AS s ON t.tenant_id = s.tenant_id
            WHEN MATCHED THEN UPDATE SET
                manifest_version = @version, content_hash = @hash, format_version = @format, models_count = @models,
                metrics_count = @metrics, manifest_json = @json, imported_by = @by, imported_at = @at
            WHEN NOT MATCHED THEN INSERT (tenant_id, manifest_version, content_hash, format_version, models_count, metrics_count, manifest_json, imported_by, imported_at)
                VALUES (@tenant, @version, @hash, @format, @models, @metrics, @json, @by, @at);";

    public async Task<StoredSemanticManifest?> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        var sql = _dialect is PostgreSqlGovernanceDialect ? SelectPg : SelectSs;
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@tenant", tenantId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredSemanticManifest(
            tenantId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task SaveAsync(TenantId tenantId, StoredSemanticManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.TenantId != tenantId)
        {
            throw new ArgumentException("The manifest belongs to another tenant.", nameof(manifest));
        }

        var sql = _dialect is PostgreSqlGovernanceDialect ? UpsertPg : UpsertSs;
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@tenant", tenantId.Value);
        cmd.AddBound("@version", manifest.Version);
        cmd.AddBound("@hash", manifest.ContentHash);
        cmd.AddBound("@format", manifest.FormatVersion);
        cmd.AddBound("@models", manifest.ModelsCount);
        cmd.AddBound("@metrics", manifest.MetricsCount);
        cmd.AddBound("@json", manifest.Json);
        cmd.AddBound("@by", manifest.ImportedBy);
        cmd.AddBound("@at", manifest.ImportedAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
