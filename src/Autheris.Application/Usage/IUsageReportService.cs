namespace Autheris.Application.Usage;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>Generates rolled-up usage reports with tariff calculation, required audit, and user erasure.</summary>
public interface IUsageReportService
{
    (DateOnly FromUtc, DateOnly ToUtc) ResolveAndValidateDateRange(string? fromRaw, string? toRaw);

    string ComputeUserKey(Sid userSid);

    Task<UsageReportResponse> GetReportAsync(
        UsageReportFilter filter,
        UsageReportAuditContext? auditContext = null,
        CancellationToken ct = default);

    Task<int> EraseUserAsync(
        TenantId tenant,
        string userSid,
        string actorIdentity,
        string? traceId = null,
        CancellationToken ct = default);
}
