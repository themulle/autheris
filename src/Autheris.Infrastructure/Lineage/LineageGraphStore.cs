namespace Autheris.Infrastructure.Lineage;

using System.Collections.Frozen;
using System.Collections.Generic;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

public sealed class LineageGraphStore : ILineageGraphStore
{
    private FrozenDictionary<string, LineageNode> _nodes = FrozenDictionary<string, LineageNode>.Empty;

    public LineageNode? GetNode(string nodeId)
    {
        return _nodes.GetValueOrDefault(nodeId);
    }

    public bool ContainsNode(string nodeId) => _nodes.ContainsKey(nodeId);

    public void UpdateGraph(IEnumerable<LineageNode> nodes)
    {
        _nodes = nodes.ToFrozenDictionary(n => n.Id, n => n);
    }

    public IReadOnlyCollection<LineageNode> GetAllNodes() => _nodes.Values;

    public int Count => _nodes.Count;
}
