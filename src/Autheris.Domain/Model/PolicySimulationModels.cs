namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public sealed record PolicySimulationRequest(
    string DraftPolicyCsv,
    TenantId? Tenant = null,
    string? TargetTable = null,
    DateTimeOffset? Since = null,
    int Limit = 500
);

public sealed record PolicySimulationDifference(
    Guid AuditLogId,
    DateTimeOffset OccurredAt,
    Sid ActorSid,
    string TargetTable,
    string? TargetColumn,
    string HistoricalDecision, // "ALLOW" or "DENY"
    string SimulatedDecision,  // "ALLOW" or "DENY"
    string Explanation
);

public sealed record TableSimulationSummary(
    string TableName,
    int EvaluatedCount,
    int AllowedCount,
    int DeniedCount,
    int ChangedCount
);

public sealed record PolicySimulationResult(
    int TotalEvaluatedLogs,
    int AllowedInBaseline,
    int DeniedInBaseline,
    int AllowedInSimulation,
    int DeniedInSimulation,
    int NewlyDeniedCount,
    int NewlyAllowedCount,
    double ImpactPercentage,
    IReadOnlyDictionary<string, TableSimulationSummary> PerTableSummaries,
    IReadOnlyList<PolicySimulationDifference> Differences,
    DateTimeOffset SimulatedAt
);
