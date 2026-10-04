namespace Autheris.Application.FinOps.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// F-AI-08: FOCUS-compliant FinOps Accounting Service.
/// Handles runtime cost calculation, token and compute metering, budget enforcement, and FOCUS reporting.
/// </summary>
public interface IFinOpsAccountingService
{
    bool IsEnabled { get; }

    ValueTask RecordUsageAsync(
        string tenantId,
        string principalId,
        string operationName,
        string category,
        long promptTokens,
        long completionTokens,
        long computeMs,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken ct = default);

    ValueTask<BudgetStatus> CheckBudgetAsync(string tenantId, CancellationToken ct = default);

    IAsyncEnumerable<FocusCostRecord> GetRecordsAsync(
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string? tenantId = null,
        CancellationToken ct = default);

    ValueTask ResetSpendAsync(string tenantId, CancellationToken ct = default);
}
