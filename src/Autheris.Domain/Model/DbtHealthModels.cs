namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public enum DbtModelHealthStatus
{
    Healthy = 0,
    Degraded = 1,
    Quarantined = 2
}

public sealed record DbtTestFailure(
    string TestName,
    string ModelName,
    string? ColumnName,
    string Severity, // "error" | "warn"
    string? Message,
    int? FailedRowsCount
);

public sealed record DbtModelExecutionResult(
    string UniqueId,
    string Status, // "success", "error", "skipped", "pass", "fail", "warn"
    TimeSpan ExecutionTime,
    int? FailuresCount,
    string? Message
);

public sealed record DbtRunResultsReport(
    string DbtVersion,
    DateTimeOffset GeneratedAt,
    TimeSpan ElapsedTime,
    IReadOnlyList<DbtModelExecutionResult> Results
);

public sealed record DbtHealthState(
    TableIdentifier Table,
    DbtModelHealthStatus Status,
    IReadOnlyList<DbtTestFailure> ActiveFailures,
    DateTimeOffset LastEvaluatedAt
);
