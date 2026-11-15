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
    Task<IReadOnlyList<ExpiringGrantCandidate>> GetGrantsWithoutExpiryNoticeAsync(
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<bool> MarkExpiryNoticeSentAsync(
        ExpiringGrantCandidate candidate,
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
