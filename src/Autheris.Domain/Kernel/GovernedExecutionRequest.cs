namespace Autheris.Domain.Kernel;

using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>
/// Architecture Phase 2: Unified request contract for all data pipelines (Relational, DuckDB, Arrow).
/// </summary>
public sealed record GovernedExecutionRequest(
    TableIdentifier TargetTable,
    IReadOnlyList<string>? RequestedColumns,
    string? SqlPredicate,
    int? RequestedLimit,
    ExecutionEngineType TargetEngine,
    IReadOnlyDictionary<string, object?>? EngineParameters = null
);
