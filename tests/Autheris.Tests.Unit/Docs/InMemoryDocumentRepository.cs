namespace Autheris.Tests.Unit.Docs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Deterministic in-memory <see cref="IDocumentRepository"/> fake for unit testing
/// <see cref="Autheris.Application.Docs.Services.GovernanceDocStore"/> and
/// <see cref="Autheris.Application.Docs.Services.DocIndexRefreshService"/> without mocks or filesystem I/O.
/// </summary>
internal sealed class InMemoryDocumentRepository : IDocumentRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Key, DocRecord> _rows = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryDocumentRepository(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool FailNextRead { get; set; }

    public List<DateTimeOffset> PurgeCutoffs { get; } = [];

    public List<DateTimeOffset?> ReadWatermarks { get; } = [];

    public Task<DocWriteOutcome> CreateAsync(DocRecord record, DocQuota quota, CancellationToken ct = default)
        => CreateManyAsync([record], quota, ct);

    public Task<DocWriteOutcome> CreateManyAsync(IReadOnlyList<DocRecord> records, DocQuota quota, CancellationToken ct = default)
    {
        if (records.Count == 0)
        {
            return Task.FromResult(new DocWriteOutcome(DocWriteStatus.Created));
        }

        lock (_gate)
        {
            var snapshot = new Dictionary<Key, DocRecord>(_rows);
            DocRecord? last = null;
            var now = _timeProvider.GetUtcNow();

            foreach (var raw in records)
            {
                var updatedAt = raw.UpdatedAt == default ? now : raw.UpdatedAt;
                var tenant = NormalizeTenant(raw.TenantId);
                var doc = raw with { TenantId = tenant, UpdatedAt = updatedAt, DeletedAt = null };

                if (tenant is not null)
                {
                    var officialKey = new Key(null, doc.AppId, doc.Category, doc.DocId);
                    if (_rows.TryGetValue(officialKey, out var official) && !official.IsDeleted)
                    {
                        Restore(snapshot);
                        return Task.FromResult(new DocWriteOutcome(DocWriteStatus.ReservedKey));
                    }
                }

                var key = new Key(tenant, doc.AppId, doc.Category, doc.DocId);
                long nextVersion = 1;
                if (_rows.TryGetValue(key, out var existing))
                {
                    if (!existing.IsDeleted)
                    {
                        Restore(snapshot);
                        return Task.FromResult(new DocWriteOutcome(DocWriteStatus.AlreadyExists));
                    }

                    nextVersion = existing.Version + 1;
                }

                if (tenant is not null)
                {
                    var usage = ComputeUsage(tenant);
                    if (usage.DocCount + 1 > quota.MaxDocumentsPerTenant ||
                        usage.TotalBytes + doc.ByteCount > quota.MaxBytesPerTenant)
                    {
                        Restore(snapshot);
                        return Task.FromResult(new DocWriteOutcome(DocWriteStatus.QuotaExceeded));
                    }
                }

                var stored = doc with { Version = nextVersion };
                _rows[key] = stored;
                last = stored;
            }

            return Task.FromResult(new DocWriteOutcome(DocWriteStatus.Created, last));
        }
    }

    public Task<DocWriteOutcome> ReplaceAsync(DocRecord record, long? expectedVersion, DocQuota quota, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var tenant = NormalizeTenant(record.TenantId);
            var key = new Key(tenant, record.AppId, record.Category, record.DocId);
            if (!_rows.TryGetValue(key, out var existing) || existing.IsDeleted)
            {
                return Task.FromResult(new DocWriteOutcome(DocWriteStatus.NotFound));
            }

            if (expectedVersion.HasValue && existing.Version != expectedVersion.Value)
            {
                return Task.FromResult(new DocWriteOutcome(DocWriteStatus.VersionMismatch));
            }

            if (tenant is not null)
            {
                var usage = ComputeUsage(tenant);
                var deltaBytes = (long)record.ByteCount - existing.ByteCount;
                if (usage.TotalBytes + deltaBytes > quota.MaxBytesPerTenant)
                {
                    return Task.FromResult(new DocWriteOutcome(DocWriteStatus.QuotaExceeded));
                }
            }

            var updatedAt = record.UpdatedAt == default ? _timeProvider.GetUtcNow() : record.UpdatedAt;
            var updated = record with
            {
                TenantId = tenant,
                Version = existing.Version + 1,
                UpdatedAt = updatedAt,
                DeletedAt = null
            };
            _rows[key] = updated;
            return Task.FromResult(new DocWriteOutcome(DocWriteStatus.Replaced, updated));
        }
    }

    public Task<DocWriteOutcome> DeleteAsync(DocKey key, long? expectedVersion, DateTimeOffset deletedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var tenant = NormalizeTenant(key.TenantId);
            var dictKey = new Key(tenant, key.AppId, key.Category, key.DocId);
            if (!_rows.TryGetValue(dictKey, out var existing) || existing.IsDeleted)
            {
                return Task.FromResult(new DocWriteOutcome(DocWriteStatus.NotFound));
            }

            if (expectedVersion.HasValue && existing.Version != expectedVersion.Value)
            {
                return Task.FromResult(new DocWriteOutcome(DocWriteStatus.VersionMismatch));
            }

            var at = deletedAt == default ? _timeProvider.GetUtcNow() : deletedAt;
            _rows[dictKey] = existing with { Version = existing.Version + 1, UpdatedAt = at, DeletedAt = at };
            return Task.FromResult(new DocWriteOutcome(DocWriteStatus.Deleted));
        }
    }

    public Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct = default)
    {
        var normalized = tenantId.Trim();
        lock (_gate)
        {
            var toRemove = _rows.Keys.Where(k => string.Equals(k.TenantId, normalized, StringComparison.Ordinal)).ToList();
            foreach (var k in toRemove)
            {
                _rows.Remove(k);
            }
            return Task.FromResult(toRemove.Count);
        }
    }

    public Task<DocRecord?> GetAsync(DocKey key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var dictKey = new Key(NormalizeTenant(key.TenantId), key.AppId, key.Category, key.DocId);
            return Task.FromResult(_rows.TryGetValue(dictKey, out var row) && !row.IsDeleted ? row : null);
        }
    }

    public Task<IReadOnlyList<DocRecord>> ReadChangedSinceAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        ReadWatermarks.Add(since);
        if (FailNextRead)
        {
            FailNextRead = false;
            throw new InvalidOperationException("Simulated repository read failure.");
        }

        lock (_gate)
        {
            var items = _rows.Values
                .Where(r => !since.HasValue || r.UpdatedAt >= since.Value)
                .ToList();
            return Task.FromResult<IReadOnlyList<DocRecord>>(items);
        }
    }

    public Task<int> PurgeTombstonesAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        PurgeCutoffs.Add(cutoff);
        lock (_gate)
        {
            var expired = _rows.Where(kv => kv.Value.IsDeleted && kv.Value.DeletedAt!.Value < cutoff).Select(kv => kv.Key).ToList();
            foreach (var k in expired)
            {
                _rows.Remove(k);
            }
            return Task.FromResult(expired.Count);
        }
    }

    public Task<DocTenantUsage> GetTenantUsageAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var (count, bytes) = ComputeUsage(tenantId.Trim());
            return Task.FromResult(new DocTenantUsage(tenantId.Trim(), count, bytes, _timeProvider.GetUtcNow()));
        }
    }

    private (int DocCount, long TotalBytes) ComputeUsage(string tenantId)
    {
        var active = _rows.Values.Where(r => !r.IsDeleted && string.Equals(r.TenantId, tenantId, StringComparison.Ordinal)).ToList();
        return (active.Count, active.Sum(r => (long)r.ByteCount));
    }

    private void Restore(Dictionary<Key, DocRecord> snapshot)
    {
        _rows.Clear();
        foreach (var kv in snapshot)
        {
            _rows[kv.Key] = kv.Value;
        }
    }

    private static string? NormalizeTenant(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();

    private readonly record struct Key(string? TenantId, string AppId, DocCategory Category, string DocId)
    {
        public string TenantKey => TenantId ?? string.Empty;
        public string AppKey => AppId.ToLowerInvariant();
        public string DocKey => DocId.ToLowerInvariant();

        public bool Equals(Key other) =>
            string.Equals(TenantKey, other.TenantKey, StringComparison.Ordinal) &&
            string.Equals(AppKey, other.AppKey, StringComparison.Ordinal) &&
            Category == other.Category &&
            string.Equals(DocKey, other.DocKey, StringComparison.Ordinal);

        public override int GetHashCode() =>
            HashCode.Combine(TenantKey, AppKey, Category, DocKey);
    }
}
