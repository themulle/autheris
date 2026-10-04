namespace Autheris.Domain.Kernel;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

public sealed record ExecutionMetrics(
    TimeSpan Duration,
    long RowCount,
    long EstimatedBytes
);

/// <summary>
/// Architecture Phase 2: Unified result contract holding normalized table, projected columns,
/// streamed rows, and the authoritative TableAccessDecision.
/// </summary>
public sealed record GovernedExecutionResult(
    TableIdentifier NormalizedTable,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    TableAccessDecision AccessDecision,
    ExecutionMetrics Metrics
);
