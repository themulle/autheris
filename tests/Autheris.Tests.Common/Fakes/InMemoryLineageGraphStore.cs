using Autheris.Application.Lineage;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// In-memory <see cref="ILineageGraphStore"/> for unit tests.
/// </summary>
public sealed class InMemoryLineageGraphStore : ILineageGraphStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LineageNode> _nodes = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _nodes.Count;
            }
        }
    }

    public LineageNode? GetNode(string nodeId)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(nodeId, out var node) ? node : null;
        }
    }

    public bool ContainsNode(string nodeId)
    {
        lock (_gate)
        {
            return _nodes.ContainsKey(nodeId);
        }
    }

    public void UpdateGraph(IEnumerable<LineageNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        lock (_gate)
        {
            foreach (var node in nodes)
            {
                _nodes[node.Id] = node;
            }
        }
    }

    public IReadOnlyCollection<LineageNode> GetAllNodes()
    {
        lock (_gate)
        {
            return _nodes.Values.ToArray();
        }
    }
}
