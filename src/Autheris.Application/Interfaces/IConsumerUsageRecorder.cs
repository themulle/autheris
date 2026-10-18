namespace Autheris.Application.Interfaces;

using Autheris.Domain.Common;
using Autheris.Domain.Security;

/// <summary>
/// Categorized outcome of a governed execution or policy evaluation for consumer telemetry.
/// </summary>
public enum ConsumerOutcome
{
    Allow,
    DenyAccess,
    DenyRateLimit,
    DenyCost,
    DenyOther
}

/// <summary>
/// Architecture Phase 2 / Issue #114: Lightweight, non-blocking telemetry recorder seam for dataset consumption metrics.
/// Pre-aggregates daily metrics by normalized application, version, and UTC day.
/// </summary>
public interface IConsumerUsageRecorder
{
    void RecordUsage(
        TableIdentifier tableId,
        TenantId tenantId,
        Sid actorSid,
        ConsumerOutcome outcome,
        long rowsReturned,
        bool rowsMeasured,
        CallerContext? caller = null);
}
