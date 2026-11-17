namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Infrastructure.Persistence.Docs;

public partial class GovernanceRepository : IDocumentRepository
{
    internal DbCommand CreateDocCommand(DbConnection conn, DbTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    public Task<DocWriteOutcome> CreateAsync(DocRecord record, DocQuota quota, CancellationToken ct = default)
        => CreateManyAsync([record ?? throw new ArgumentNullException(nameof(record))], quota ?? throw new ArgumentNullException(nameof(quota)), ct);

    public Task<DocWriteOutcome> UpsertOfficialManyAsync(IReadOnlyList<DocRecord> records, CancellationToken ct = default)
        => DocumentStoreOperations.UpsertOfficialManyAsync(this, records ?? throw new ArgumentNullException(nameof(records)), ct);

    public Task<DocWriteOutcome> CreateManyAsync(IReadOnlyList<DocRecord> records, DocQuota quota, CancellationToken ct = default)
        => DocumentStoreOperations.CreateManyAsync(this, records ?? throw new ArgumentNullException(nameof(records)), quota ?? throw new ArgumentNullException(nameof(quota)), ct);

    public Task<DocWriteOutcome> ReplaceAsync(DocRecord record, long? expectedVersion, DocQuota quota, CancellationToken ct = default)
        => DocumentStoreOperations.ReplaceAsync(this, record ?? throw new ArgumentNullException(nameof(record)), expectedVersion, quota ?? throw new ArgumentNullException(nameof(quota)), ct);

    public Task<DocWriteOutcome> DeleteAsync(DocKey key, long? expectedVersion, DateTimeOffset deletedAt, CancellationToken ct = default)
        => DocumentStoreOperations.DeleteAsync(this, key ?? throw new ArgumentNullException(nameof(key)), expectedVersion, deletedAt, ct);

    public Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct = default)
        => DocumentStoreOperations.PurgeTenantAsync(this, tenantId, ct);

    public Task<DocRecord?> GetAsync(DocKey key, CancellationToken ct = default)
        => DocumentStoreOperations.GetAsync(this, key ?? throw new ArgumentNullException(nameof(key)), ct);

    public Task<IReadOnlyList<DocRecord>> ReadChangedSinceAsync(DateTimeOffset? since, CancellationToken ct = default)
        => DocumentStoreOperations.ReadChangedSinceAsync(this, since, ct);

    public Task<int> PurgeTombstonesAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => DocumentStoreOperations.PurgeTombstonesAsync(this, cutoff, ct);

    public Task<DocTenantUsage> GetTenantUsageAsync(string tenantId, CancellationToken ct = default)
        => DocumentStoreOperations.GetTenantUsageAsync(this, tenantId, ct);
}
