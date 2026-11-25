using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Governance.Grants;

/// <summary>Result of <see cref="IGrantRevocationService.RevokeAllForSubjectAsync"/>.</summary>
public sealed record GrantRevocationResult(
    int GrantsRevoked,
    int CatalogGrantsRevoked,
    int RequestsWithdrawn,
    int Failed,
    bool Truncated)
{
    /// <summary>True only when nothing failed and nothing remains for a retry.</summary>
    public bool Complete => Failed == 0 && !Truncated;
}

/// <summary>Thrown when deprovisioning could not revoke every grant; the subject stays locked and the call is retried.</summary>
public sealed class GrantRevocationIncompleteException(GrantRevocationResult result)
    : InvalidOperationException("Grant revocation for the deprovisioned subject is incomplete; the subject stays locked and the operation must be retried.")
{
    public GrantRevocationResult Result { get; } = result;
}

public interface IGrantRevocationService
{
    /// <summary>
    /// Revokes all data, unmask and catalog grants of <paramref name="subject"/> within <paramref name="tenant"/> and withdraws
    /// its pending requests, with one audit entry per item. Revocation is terminal and the run is idempotent.
    /// </summary>
    Task<GrantRevocationResult> RevokeAllForSubjectAsync(TenantId tenant, Sid subject, string reason, CancellationToken ct = default);
}

public sealed class GrantRevocationService : IGrantRevocationService
{
    internal const string MeterName = "Autheris.Governance";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> BacklogCounter = Meter.CreateCounter<long>(
        "autheris.deprovisioning.revocation_backlog",
        description: "Grants or requests that could not be revoked during deprovisioning and remain for retry.");

    /// <summary>Grants and requests handled per run; the remainder is picked up by the idempotent retry.</summary>
    public const int DefaultMaxItemsPerRun = 1000;

    /// <summary>Wall-clock budget for one run; exceeding it leaves the run incomplete (the subject stays locked).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Hard ceiling of active Allow grants scanned per run; more than this leaves the run incomplete (subject stays locked).</summary>
    public const int MaxActiveGrantsScanned = 50_000;

    private static readonly Sid SystemActor = new("SYSTEM:DEPROVISIONING");

    private readonly IConsentRepository _consents;
    private readonly ICatalogVisibilityGrantRepository? _catalog;
    private readonly IConsentApprovalRepository _approvals;
    private readonly IAuditWriter _audit;
    private readonly int _maxItemsPerRun;
    private readonly int _maxActiveGrantsScanned;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly ILogger<GrantRevocationService>? _logger;

    public GrantRevocationService(
        IConsentRepository consents,
        IConsentApprovalRepository approvals,
        IAuditWriter audit,
        ICatalogVisibilityGrantRepository? catalog = null,
        int maxItemsPerRun = DefaultMaxItemsPerRun,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null,
        ILogger<GrantRevocationService>? logger = null,
        int maxActiveGrantsScanned = MaxActiveGrantsScanned)
    {
        _consents = consents ?? throw new ArgumentNullException(nameof(consents));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _catalog = catalog;
        _maxItemsPerRun = Math.Max(1, maxItemsPerRun);
        _timeout = timeout ?? DefaultTimeout;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _maxActiveGrantsScanned = Math.Max(1, maxActiveGrantsScanned);
    }

    public async Task<GrantRevocationResult> RevokeAllForSubjectAsync(TenantId tenant, Sid subject, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            throw new ArgumentException("A valid tenant is required.", nameof(tenant));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subject.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_timeout);
        var token = budget.Token;
        var now = _time.GetUtcNow();
        var maxItems = _maxItemsPerRun;
        var handled = 0;
        var revoked = 0;
        var catalogRevoked = 0;
        var withdrawn = 0;
        var failed = 0;
        var truncated = false;
        var subjects = new[] { subject };

        try
        {
            // The store bounds each keyset page and returns only Allow rows, so Deny rows never consume the
            // scan ceiling; seenConsentIds prevents repeating a failing row across pages or broken fakes.
            const int defaultBatchSize = 200;
            var scanned = 0;
            var seenConsentIds = new HashSet<Guid>();
            Guid? afterId = null;

            while (!token.IsCancellationRequested && handled < maxItems)
            {
                var remainingScanBudget = _maxActiveGrantsScanned - scanned;
                if (remainingScanBudget <= 0)
                {
                    truncated = true;
                    break;
                }

                var pageSize = Math.Min(remainingScanBudget + 1, defaultBatchSize);
                var page = (afterId is null
                    ? await _consents.GetActiveGrantsForSubjectsAsync(tenant, subjects, now, pageSize, token).ConfigureAwait(false)
                    : await _consents.GetActiveGrantsForSubjectsAfterAsync(tenant, subjects, now, afterId, pageSize, token).ConfigureAwait(false))
                    ?? Array.Empty<Consent>();

                if (page.Count == 0)
                {
                    break;
                }

                scanned += page.Count;
                if (scanned > _maxActiveGrantsScanned)
                {
                    truncated = true;
                    _logger?.LogWarning("Deprovisioning revocation scanned more than {Ceiling} active grants; the run is incomplete and will be retried.", _maxActiveGrantsScanned);
                }

                var addedNew = false;
                Guid? lastId = null;
                foreach (var consent in page)
                {
                    lastId = consent.Id;
                    if (!seenConsentIds.Add(consent.Id))
                    {
                        continue;
                    }

                    addedNew = true;
                    if (!IsRevocableGrant(consent))
                    {
                        continue;
                    }

                    if (handled++ >= maxItems)
                    {
                        truncated = true;
                        break;
                    }

                    if (await TryRevokeGrantAsync(tenant, subject, consent, reason, token).ConfigureAwait(false))
                    {
                        revoked++;
                    }
                    else
                    {
                        failed++;
                    }
                }

                if (truncated || handled >= maxItems || !addedNew || page.Count < pageSize || lastId == afterId)
                {
                    break;
                }

                afterId = lastId;
            }

            if (_catalog is not null && !truncated)
            {
                var seenCatalogIds = new HashSet<Guid>();
                var catalogGrants = await _catalog.ListForDeprovisioningAsync(tenant, subjects, now, token).ConfigureAwait(false)
                    ?? Array.Empty<CatalogVisibilityGrant>();
                foreach (var grant in catalogGrants)
                {
                    if (grant.IsRevoked || !seenCatalogIds.Add(grant.Id))
                    {
                        continue;
                    }

                    if (handled++ >= maxItems)
                    {
                        truncated = true;
                        break;
                    }

                    if (await TryRevokeCatalogGrantAsync(tenant, subject, grant, reason, now, token).ConfigureAwait(false))
                    {
                        catalogRevoked++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }

            if (!truncated)
            {
                var seenRequestIds = new HashSet<Guid>();
                var pending = await _approvals.GetPendingRequestsForSubjectAsync(subject, tenant, token).ConfigureAwait(false)
                    ?? Array.Empty<ConsentRequest>();
                foreach (var request in pending)
                {
                    if (!IsWithdrawableRequestForSubject(request, subject) || !seenRequestIds.Add(request.Id))
                    {
                        continue;
                    }

                    if (handled++ >= maxItems)
                    {
                        truncated = true;
                        break;
                    }

                    if (await TryWithdrawAsync(tenant, subject, request, reason, token).ConfigureAwait(false))
                    {
                        withdrawn++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            truncated = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Grant enumeration failed during deprovisioning revocation.");
            failed++;
        }

        var result = new GrantRevocationResult(revoked, catalogRevoked, withdrawn, failed, truncated);
        if (!result.Complete)
        {
            BacklogCounter.Add(Math.Max(1, failed));
        }

        return result;
    }

    private static bool IsRevocableGrant(Consent consent) =>
        !consent.IsRevoked &&
        consent.Effect == ConsentEffect.Allow &&
        consent.GrantKind is PermissionGrantKind.Data or PermissionGrantKind.Unmask;

    private static bool IsWithdrawableRequestForSubject(ConsentRequest request, Sid subject)
    {
        if (!string.Equals(request.Status, "PENDING", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Status, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.RequesterSid == subject)
        {
            return true;
        }

        return request.RequestedGranteeType != GranteeType.Group &&
               !string.IsNullOrWhiteSpace(request.RequestedGranteeRef) &&
               new Sid(request.RequestedGranteeRef.Trim()) == subject;
    }

    private async Task<bool> TryRevokeGrantAsync(TenantId tenant, Sid subject, Consent consent, string reason, CancellationToken ct)
    {
        try
        {
            var target = !string.IsNullOrWhiteSpace(consent.TableIdentifier.TableName)
                ? consent.TableIdentifier.ToString()
                : consent.ObjectPattern ?? consent.Id.ToString("D");
            await AuditItemAsync(
                AuditEventTypes.GrantRevokedDeprovisioned, tenant, subject, target, consent.Id, consent.GrantKind.ToSqlString(), reason, ct).ConfigureAwait(false);
            var done = await _consents.RevokeSystemConsentByIdAsync(tenant, consent.Id, SystemActor, reason, ct).ConfigureAwait(false);
            return done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Revoking grant {GrantId} during deprovisioning failed; it will be retried.", consent.Id);
            return false;
        }
    }

    private async Task<bool> TryRevokeCatalogGrantAsync(TenantId tenant, Sid subject, CatalogVisibilityGrant grant, string reason, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await AuditItemAsync(
                AuditEventTypes.GrantRevokedDeprovisioned, tenant, subject, $"{grant.ContainerKind}:{grant.ContainerId:D}", grant.Id, PermissionGrantKind.Catalog.ToSqlString(), reason, ct).ConfigureAwait(false);
            var done = await _catalog!.RevokeAsync(tenant, grant.Id, SystemActor, reason, now, null, ct).ConfigureAwait(false);
            return done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Revoking catalog grant {GrantId} during deprovisioning failed; it will be retried.", grant.Id);
            return false;
        }
    }

    private async Task<bool> TryWithdrawAsync(TenantId tenant, Sid subject, ConsentRequest request, string reason, CancellationToken ct)
    {
        try
        {
            await AuditItemAsync(
                AuditEventTypes.ConsentWithdrawn, tenant, subject, request.TableIdentifier.TableName.Length > 0 ? request.TableIdentifier.ToString() : request.Id.ToString("D"), request.Id, request.GrantKind.ToSqlString(), reason, ct).ConfigureAwait(false);
            var done = await _approvals.WithdrawConsentRequestAsync(request.Id, request.RequesterSid, ct).ConfigureAwait(false);
            return done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Withdrawing request {RequestId} during deprovisioning failed; it will be retried.", request.Id);
            return false;
        }
    }

    private Task AuditItemAsync(string eventType, TenantId tenant, Sid subject, string target, Guid itemId, string grantKind, string reason, CancellationToken ct) =>
        _audit.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = _time.GetUtcNow(),
            EventType = eventType,
            ActorSid = SystemActor,
            TargetTable = target,
            Decision = "REVOKED",
            TraceId = Guid.NewGuid().ToString("N"),
            TenantId = tenant,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                subject = subject.Value,
                item_id = itemId,
                grant_kind = grantKind,
                reason
            })
        }, ct);
}
