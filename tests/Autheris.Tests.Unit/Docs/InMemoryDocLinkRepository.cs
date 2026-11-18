namespace Autheris.Tests.Unit.Docs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;

/// <summary>Deterministic in-memory <see cref="IDocLinkRepository"/> fake with the tenant scoping of the real store.</summary>
internal sealed class InMemoryDocLinkRepository : IDocLinkRepository
{
    private readonly object _gate = new();
    private readonly List<DocLinkRecord> _rows = [];

    public IReadOnlyList<DocLinkRecord> Snapshot()
    {
        lock (_gate) return [.. _rows];
    }

    public Task<IReadOnlyList<DocLinkRecord>> ListForTargetsAsync(string tenantId, IReadOnlyList<DocLinkTarget> targets, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DocLinkRecord>>(_rows
                .Where(r => r.TenantId == tenantId && targets.Any(t => t.Domain == r.Target.Domain && t.Schema == r.Target.Schema && t.Name == r.Target.Name))
                .ToList());
        }
    }

    public Task<IReadOnlyList<DocLinkRecord>> ListForSourceAsync(string tenantId, DocLinkSource source, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<DocLinkRecord>>(_rows.Where(r => r.TenantId == tenantId && r.Source == source).ToList());
    }

    public Task ReplaceForSourceAsync(string tenantId, DocLinkSource source, IReadOnlyList<DocLinkTarget> targets, string actor, CancellationToken ct = default)
    {
        if (targets.Distinct().Count() > DocLinkLimits.MaxTargetsPerSource) throw new ArgumentException("Too many link targets.", nameof(targets));
        lock (_gate)
        {
            _rows.RemoveAll(r => r.TenantId == tenantId && r.Source == source);
            _rows.AddRange(targets.Distinct().Select(t => new DocLinkRecord(tenantId, Guid.NewGuid(), source, t, actor, DateTimeOffset.UtcNow)));
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteForSourceAsync(string tenantId, DocLinkSource source, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_rows.RemoveAll(r => r.TenantId == tenantId && r.Source == source));
    }

    internal void RemoveForDeletedDocument(DocKey key)
    {
        var source = DocLinkSource.ForDocument(key);
        lock (_gate) _rows.RemoveAll(r => r.Source == source);
    }

    internal void RemoveForTenant(string tenantId)
    {
        lock (_gate) _rows.RemoveAll(r => r.TenantId == tenantId);
    }
}
