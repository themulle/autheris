namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;
using System.Globalization;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed class DiscoveryArtifactStore : IDiscoveryArtifactStore
{
    private readonly IGovernanceRepository _repository;

    public DiscoveryArtifactStore(IGovernanceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<DiscoveryArtifacts?> GetAsync(Guid tableId, CancellationToken ct = default) =>
        _repository.GetDiscoveryArtifactsAsync(tableId, ct);

    public Task<DiscoveryArtifacts?> GetAsync(Guid tableId, ArtifactProvenance provenance, CancellationToken ct = default) =>
        _repository.GetDiscoveryArtifactsAsync(tableId, provenance, ct);

    public Task<IReadOnlyList<DiscoveryArtifacts>> GetAllAsync(Guid tableId, CancellationToken ct = default) =>
        _repository.GetAllDiscoveryArtifactsAsync(tableId, ct);

    public Task<bool> UpsertAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default) =>
        _repository.UpsertDiscoveryArtifactsAsync(artifacts, actor, ct);

    public Task<bool> DeleteAsync(Guid tableId, Sid actor, CancellationToken ct = default) =>
        _repository.DeleteDiscoveryArtifactsAsync(tableId, actor, ct);

    public Task<bool> DeleteAsync(Guid tableId, ArtifactProvenance provenance, Sid actor, CancellationToken ct = default) =>
        _repository.DeleteDiscoveryArtifactsAsync(tableId, provenance, actor, ct);

    internal static async Task<DiscoveryArtifacts?> GetAsync(DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid tableId, CancellationToken ct = default)
    {
        var all = await GetAllAsync(conn, tx, dialect, tableId, ct).ConfigureAwait(false);
        return all.FirstOrDefault(a => a.Provenance == ArtifactProvenance.Upload)
            ?? all.FirstOrDefault(a => a.Provenance == ArtifactProvenance.Unknown)
            ?? all.FirstOrDefault();
    }

    internal static async Task<DiscoveryArtifacts?> GetAsync(DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid tableId, ArtifactProvenance provenance, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = $"SELECT table_id, provenance, tenant_id, samples_json, statistics_json, content_sha256, uploaded_by, uploaded_at, schema_version FROM {dialect.Prefix}CATALOG_DISCOVERY_ARTIFACTS WHERE table_id = @tid AND provenance = @prov";
        AddParameter(cmd, "@tid", tableId.ToString());
        AddParameter(cmd, "@prov", provenance.ToDbString());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return ReadArtifact(tableId, reader);
    }

    internal static async Task<IReadOnlyList<DiscoveryArtifacts>> GetAllAsync(DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid tableId, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = $"SELECT table_id, provenance, tenant_id, samples_json, statistics_json, content_sha256, uploaded_by, uploaded_at, schema_version FROM {dialect.Prefix}CATALOG_DISCOVERY_ARTIFACTS WHERE table_id = @tid ORDER BY provenance";
        AddParameter(cmd, "@tid", tableId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<DiscoveryArtifacts>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadArtifact(tableId, reader));
        }

        return rows;
    }

    private static DiscoveryArtifacts ReadArtifact(Guid tableId, DbDataReader reader)
    {
        var provenance = ArtifactProvenanceExtensions.Parse(reader.GetString(1));
        var tenantId = reader.GetString(2);
        var samples = reader.IsDBNull(3) ? null : reader.GetString(3);
        var stats = reader.IsDBNull(4) ? null : reader.GetString(4);
        var sha = reader.GetString(5);
        var uploadedBy = reader.GetString(6);
        var uploadedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
        var schemaVersion = reader.GetInt32(8);
        return new DiscoveryArtifacts(tableId, tenantId, samples, stats, sha, uploadedBy, uploadedAt, schemaVersion, provenance);
    }

    internal static async Task<bool> UpsertAsync(DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Provenance == ArtifactProvenance.Import && !string.IsNullOrWhiteSpace(artifacts.StatisticsJson))
        {
            throw new ArgumentException("Import discovery artifacts must not carry statistics.", nameof(artifacts));
        }

        var provenance = artifacts.Provenance.ToDbString();
        var parameters = new (string Name, object Value)[]
        {
            ("@tid", artifacts.TableId.ToString()),
            ("@prov", provenance),
            ("@tenant", artifacts.TenantId),
            ("@samples", (object?)artifacts.SamplesJson ?? DBNull.Value),
            ("@stats", (object?)artifacts.StatisticsJson ?? DBNull.Value),
            ("@sha", artifacts.ContentSha256),
            ("@by", string.IsNullOrWhiteSpace(artifacts.UploadedBy) ? actor.Value : artifacts.UploadedBy),
            ("@at", artifacts.UploadedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("@ver", artifacts.SchemaVersion)
        };

        var updated = await ExecAsync(conn, tx,
            $"UPDATE {dialect.Prefix}CATALOG_DISCOVERY_ARTIFACTS SET tenant_id = @tenant, samples_json = @samples, statistics_json = @stats, content_sha256 = @sha, uploaded_by = @by, uploaded_at = @at, schema_version = @ver WHERE table_id = @tid AND provenance = @prov",
            ct, parameters).ConfigureAwait(false);

        if (updated == 0)
        {
            await ExecAsync(conn, tx,
                $"INSERT INTO {dialect.Prefix}CATALOG_DISCOVERY_ARTIFACTS (table_id, provenance, tenant_id, samples_json, statistics_json, content_sha256, uploaded_by, uploaded_at, schema_version) VALUES (@tid, @prov, @tenant, @samples, @stats, @sha, @by, @at, @ver)",
                ct, parameters).ConfigureAwait(false);
        }

        return true;
    }

    internal static async Task<bool> DeleteAsync(DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid tableId, Sid actor, CancellationToken ct = default)
    {
        var deleted = await ExecAsync(conn, tx,
            $"DELETE FROM {dialect.Prefix}CATALOG_DISCOVERY_ARTIFACTS WHERE table_id = @tid",
            ct, ("@tid", tableId.ToString())).ConfigureAwait(false);
        return deleted > 0;
    }

    internal static async Task<bool> DeleteAsync(DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid tableId, ArtifactProvenance provenance, Sid actor, CancellationToken ct = default)
    {
        var deleted = await ExecAsync(conn, tx,
            $"DELETE FROM {dialect.Prefix}CATALOG_DISCOVERY_ARTIFACTS WHERE table_id = @tid AND provenance = @prov",
            ct, ("@tid", tableId.ToString()), ("@prov", provenance.ToDbString())).ConfigureAwait(false);
        return deleted > 0;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    private static async Task<int> ExecAsync(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            AddParameter(cmd, name, value);
        }
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
