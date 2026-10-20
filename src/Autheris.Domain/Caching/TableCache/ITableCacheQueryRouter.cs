namespace Autheris.Domain.Caching.TableCache;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Evaluates whether an incoming AST query can be served fully or partially from the table cache.
/// </summary>
public interface ITableCacheQueryRouter
{
    ValueTask<TableCacheRoutingDecision> EvaluateRoutingAsync(
        TableIdentifier table,
        IReadOnlyList<string> requestedColumns,
        string? whereClause,
        CancellationToken ct = default);
}
