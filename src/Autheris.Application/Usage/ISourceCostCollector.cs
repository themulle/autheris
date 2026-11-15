namespace Autheris.Application.Usage;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Asynchronously fetches delayed source-side compute and cost telemetry for executed queries.</summary>
public interface ISourceCostCollector
{
    /// <summary>Provider identifier handled by this collector (for example, <c>Databricks</c>).</summary>
    string SourceType { get; }

    /// <summary>Canonical unit of <see cref="SourceCostSample.SourceCost"/> emitted by this collector (for example, <c>DBU</c>).</summary>
    string CostUnit { get; }

    /// <summary>Fetches available compute and cost samples for a batch of pending source queries.</summary>
    Task<IReadOnlyList<SourceCostSample>> CollectAsync(IReadOnlyList<PendingSourceQuery> pending, CancellationToken ct);
}
