namespace Autheris.Infrastructure.Lineage;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

public sealed class LineageGraphStore : ILineageGraphStore
{
    private readonly object _gate = new();
    private FrozenDictionary<string, LineageNode> _nodes = FrozenDictionary<string, LineageNode>.Empty;

    public LineageNode? GetNode(string nodeId)
    {
        return _nodes.GetValueOrDefault(nodeId);
    }

    public bool ContainsNode(string nodeId) => _nodes.ContainsKey(nodeId);

    /// <summary>
    /// SEC SR-L-19: replaces the nodes of the tenants present in <paramref name="nodes"/> (untenanted nodes form their own
    /// group) and retains the nodes of all other tenants, so one tenant's ingestion run cannot wipe another tenant's graph.
    /// </summary>
    public void UpdateGraph(IEnumerable<LineageNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var incoming = nodes.ToList();
        var replacedTenants = new HashSet<string?>(incoming.Select(n => NormalizeTenant(n.TenantId)), StringComparer.Ordinal);

        lock (_gate)
        {
            var merged = new Dictionary<string, LineageNode>(StringComparer.Ordinal);
            foreach (var existing in _nodes.Values)
            {
                if (!replacedTenants.Contains(NormalizeTenant(existing.TenantId)))
                {
                    merged[existing.Id] = existing;
                }
            }

            foreach (var node in incoming)
            {
                merged[node.Id] = node;
            }

            _nodes = merged.ToFrozenDictionary();
        }
    }

    private static string? NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.ToLowerInvariant();

    public IReadOnlyCollection<LineageNode> GetAllNodes() => _nodes.Values;

    public int Count => _nodes.Count;
}
