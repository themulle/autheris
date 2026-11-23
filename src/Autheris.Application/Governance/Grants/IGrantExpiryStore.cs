namespace Autheris.Application.Governance.Grants;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public sealed record ExpiringGrantCandidate(
    Consent Consent,
    ConsentWorkflowMode WorkflowMode,
    Guid? ExternalSystemId);

public sealed record GrantExpirySweepSummary(
    int NoticesSent,
    int GrantsExpired,
    int RequestsTimedOut);

/// <summary>
/// Persistence boundary for <see cref="GrantExpiryHostedService"/> sweeps covering expiry notices,
/// expired grant ledger entries, and pending request timeouts.
/// </summary>
public interface IGrantExpiryStore
{
    /// <summary>
    /// Active grants expiring between <paramref name="now"/> and <paramref name="horizon"/> whose reminder stage is below
    /// <paramref name="maxStage"/>, least-reminded and soonest-expiring first, bounded per call.
    /// </summary>
    Task<IReadOnlyList<ExpiringGrantCandidate>> GetGrantsWithPendingReminderAsync(
        DateTimeOffset now,
        DateTimeOffset horizon,
        int maxStage,
        CancellationToken ct = default);

    /// <summary>
    /// Records that reminder <paramref name="stage"/> was sent (compare-and-set: only raises the stored stage) and
    /// appends the <c>grant.expiring</c> event carrying the stage. Returns false when the stage was already recorded.
    /// </summary>
    Task<bool> MarkReminderStageSentAsync(
        ExpiringGrantCandidate candidate,
        int stage,
        DateTimeOffset sentAt,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExpiringGrantCandidate>> GetExpiredGrantsWithoutRecordAsync(
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<bool> RecordGrantExpiryAsync(
        ExpiringGrantCandidate candidate,
        DateTimeOffset expiredAt,
        CancellationToken ct = default);

    Task<IReadOnlyList<ConsentRequest>> GetOpenPendingRequestsAsync(
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<bool> ExpirePendingRequestAsync(
        ConsentRequest request,
        DateTimeOffset expiredAt,
        CancellationToken ct = default);
}
