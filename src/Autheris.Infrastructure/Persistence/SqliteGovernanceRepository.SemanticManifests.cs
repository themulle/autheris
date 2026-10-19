namespace Autheris.Infrastructure.Persistence;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Common;

/// <summary>#112 A-9: <see cref="ISemanticManifestStore"/> on the SQLite governance store (one row per tenant, single-statement upsert).</summary>
public partial class SqliteGovernanceRepository : ISemanticManifestStore
{
    public async Task<StoredSemanticManifest?> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        await using var scope = await ReadScopeAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT manifest_version, content_hash, format_version, models_count, metrics_count, manifest_json, imported_by, imported_at
            FROM SEMANTIC_MANIFESTS WHERE tenant_id = @tenant;";
        cmd.Parameters.AddWithValue("@tenant", tenantId.Value);

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

        await using var scope = await WriteScopeAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SEMANTIC_MANIFESTS (tenant_id, manifest_version, content_hash, format_version, models_count, metrics_count, manifest_json, imported_by, imported_at)
            VALUES (@tenant, @version, @hash, @format, @models, @metrics, @json, @by, @at)
            ON CONFLICT(tenant_id) DO UPDATE SET
                manifest_version = excluded.manifest_version,
                content_hash = excluded.content_hash,
                format_version = excluded.format_version,
                models_count = excluded.models_count,
                metrics_count = excluded.metrics_count,
                manifest_json = excluded.manifest_json,
                imported_by = excluded.imported_by,
                imported_at = excluded.imported_at;";
        cmd.Parameters.AddWithValue("@tenant", tenantId.Value);
        cmd.Parameters.AddWithValue("@version", manifest.Version);
        cmd.Parameters.AddWithValue("@hash", manifest.ContentHash);
        cmd.Parameters.AddWithValue("@format", manifest.FormatVersion);
        cmd.Parameters.AddWithValue("@models", manifest.ModelsCount);
        cmd.Parameters.AddWithValue("@metrics", manifest.MetricsCount);
        cmd.Parameters.AddWithValue("@json", manifest.Json);
        cmd.Parameters.AddWithValue("@by", manifest.ImportedBy);
        cmd.Parameters.AddWithValue("@at", manifest.ImportedAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
