namespace Autheris.Application.Interfaces;

using System.Collections.Generic;
using Autheris.Domain.Model;

public interface ILineageGraphStore
{
    LineageNode? GetNode(string nodeId);
    bool ContainsNode(string nodeId);
    void UpdateGraph(IEnumerable<LineageNode> nodes);
    IReadOnlyCollection<LineageNode> GetAllNodes();
    int Count { get; }
}
