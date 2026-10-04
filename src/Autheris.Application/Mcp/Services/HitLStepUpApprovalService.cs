namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Workflows;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class HitLStepUpApprovalService : IHitLStepUpApprovalService
{
    private readonly ConcurrentDictionary<string, TicketEntry> _tickets = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptions<GatewayOptions> _options;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<HitLStepUpApprovalService> _logger;
    private readonly Autheris.Application.State.IDistributedClusterStateProvider? _clusterState;

    private sealed class TicketEntry
    {
        public readonly object Lock = new();
        public HitLApprovalTicket Ticket { get; set; }
        public TaskCompletionSource<HitLApprovalResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset? CompletedAt { get; set; }

        public TicketEntry(HitLApprovalTicket ticket) => Ticket = ticket;
    }

    // SEC C-05 / M-09: Finished (approved/rejected/expired) tickets are kept only for a short audit/replay window and then purged.
    internal static readonly TimeSpan CompletedTicketRetention = TimeSpan.FromMinutes(15);

    public HitLStepUpApprovalService(
        IOptions<GatewayOptions> options,
        ILogger<HitLStepUpApprovalService> logger,
        IServiceScopeFactory? scopeFactory = null,
        Autheris.Application.State.IDistributedClusterStateProvider? clusterState = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scopeFactory = scopeFactory;
        _clusterState = clusterState;
    }

    public async Task<HitLApprovalResult> RequestStepUpApprovalAsync(
        string toolName,
        string tenantId,
        string requesterSid,
        TableIdentifier targetTable,
        string? justification = null,
        CancellationToken ct = default)
    {
        PurgeStaleTickets(DateTimeOffset.UtcNow);

        var approvalId = $"hitl-{Guid.NewGuid():N}";
        var timeoutSeconds = Math.Max(1, _options.Value.HitLStepUp.ApprovalTimeoutSeconds);
        var createdAt = DateTimeOffset.UtcNow;
        var expiresAt = createdAt.AddSeconds(timeoutSeconds);

        string? itsmTicketId = null;
        string? itsmTicketUrl = null;

        // Auto-create ITSM ticket if enabled
        if (_options.Value.HitLStepUp.AutoCreateItsmTicket && _scopeFactory != null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var itsmDispatcher = scope.ServiceProvider.GetService<ItsmWorkflowDispatcher>();
                if (itsmDispatcher != null)
                {
                    var ticketRequest = new ItsmTicketRequest(
                        Tenant: new TenantId(tenantId),
                        RequesterSid: new Sid(requesterSid),
                        TargetTable: targetTable,
                        Justification: justification ?? $"Human-in-the-Loop Step-Up approval required for MCP tool '{toolName}' on table '{targetTable}'",
                        DurationDays: 1,
                        TriageCategory: JustificationCategory.LegitimateAudit,
                        TriageConfidence: 1.0
                    );

                    var dispatchResult = await itsmDispatcher.DispatchTicketRequestAsync(
                        ticketRequest,
                        _options.Value.HitLStepUp.PreferredItsmSystem,
                        ct).ConfigureAwait(false);

                    if (dispatchResult.Success && dispatchResult.TicketReference != null)
                    {
                        itsmTicketId = dispatchResult.TicketReference.TicketId;
                        itsmTicketUrl = dispatchResult.TicketReference.TicketUrl;
                        _logger.LogInformation("Dispatched HitL ITSM ticket '{TicketId}' for approval '{ApprovalId}'.", itsmTicketId, approvalId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-create ITSM ticket for HitL approval '{ApprovalId}'. Proceeding with in-memory approval gate.", approvalId);
            }
        }

        var ticket = new HitLApprovalTicket(
            ApprovalId: approvalId,
            ToolName: toolName,
            TenantId: tenantId,
            RequesterSid: requesterSid,
            TargetTable: targetTable,
            Justification: justification,
            CreatedAt: createdAt,
            ExpiresAt: expiresAt,
            Status: HitLApprovalStatus.Pending,
            ItsmTicketId: itsmTicketId,
            ItsmTicketUrl: itsmTicketUrl
        );

        var entry = new TicketEntry(ticket);
        _tickets[approvalId] = entry;

        IAsyncDisposable? clusterSubscription = null;
        if (_clusterState != null)
        {
            try
            {
                await _clusterState.SetAsync($"hitl:ticket:{approvalId}", ticket, TimeSpan.FromSeconds(timeoutSeconds + 900), ct).ConfigureAwait(false);
                clusterSubscription = _clusterState.SubscribeAsync<HitLApprovalBroadcast>($"hitl:events:{approvalId}", broadcast =>
                {
                    lock (entry.Lock)
                    {
                        if (entry.Ticket.Status == HitLApprovalStatus.Pending)
                        {
                            entry.Ticket = entry.Ticket with
                            {
                                Status = broadcast.IsApproved ? HitLApprovalStatus.Approved : HitLApprovalStatus.Rejected,
                                ApproverSid = broadcast.ApproverSid,
                                RejectionReason = broadcast.IsApproved ? null : (broadcast.Reason ?? "Rejected by cluster decision.")
                            };
                            entry.CompletedAt = DateTimeOffset.UtcNow;
                            var result = new HitLApprovalResult(broadcast.IsApproved, entry.Ticket, broadcast.IsApproved ? "Approval granted." : (broadcast.Reason ?? "Approval rejected."));
                            entry.Tcs.TrySetResult(result);
                        }
                    }
                    return ValueTask.CompletedTask;
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register HitL ticket '{ApprovalId}' in cluster state.", approvalId);
            }
        }

        _logger.LogInformation("HitL Step-Up approval requested. ID: {ApprovalId}, Table: {Table}, Requester: {RequesterSid}, Timeout: {Timeout}s",
            approvalId, targetTable, requesterSid, timeoutSeconds);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), timeoutCts.Token);
            var completedTask = await Task.WhenAny(entry.Tcs.Task, delayTask).ConfigureAwait(false);

            if (completedTask == entry.Tcs.Task)
            {
                timeoutCts.Cancel();
                return await entry.Tcs.Task.ConfigureAwait(false);
            }
            else
            {
                // VULN-06: Fail-Closed on timeout
                lock (entry.Lock)
                {
                    if (entry.Ticket.Status == HitLApprovalStatus.Pending)
                    {
                        entry.Ticket = entry.Ticket with { Status = HitLApprovalStatus.Expired };
                    }
                }

                _logger.LogWarning("HitL Step-Up approval '{ApprovalId}' timed out after {Timeout}s. Failing closed.", approvalId, timeoutSeconds);
                var expiredResult = new HitLApprovalResult(false, entry.Ticket, $"Approval timed out after {timeoutSeconds}s.");
                entry.Tcs.TrySetResult(expiredResult);
                return expiredResult;
            }
        }
        catch (OperationCanceledException)
        {
            lock (entry.Lock)
            {
                if (entry.Ticket.Status == HitLApprovalStatus.Pending)
                {
                    entry.Ticket = entry.Ticket with { Status = HitLApprovalStatus.Expired };
                }
            }

            var cancelledResult = new HitLApprovalResult(false, entry.Ticket, "Approval request was canceled.");
            entry.Tcs.TrySetResult(cancelledResult);
            return cancelledResult;
        }
        finally
        {
            if (clusterSubscription != null)
            {
                try { await clusterSubscription.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            }
            // SEC M-09: Mark the ticket as finished so it is purged after the retention window (no unbounded growth).
            lock (entry.Lock)
            {
                entry.CompletedAt ??= DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// SEC M-09: Removes finished tickets after <see cref="CompletedTicketRetention"/> and pending tickets whose
    /// expiry lies further back than the retention window (e.g. orphaned by a crashed request).
    /// </summary>
    internal int PurgeStaleTickets(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var kvp in _tickets)
        {
            var entry = kvp.Value;
            bool stale;
            lock (entry.Lock)
            {
                stale = (entry.CompletedAt.HasValue && now - entry.CompletedAt.Value > CompletedTicketRetention) ||
                        now - entry.Ticket.ExpiresAt > CompletedTicketRetention;
            }

            if (stale && _tickets.TryRemove(kvp.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    internal int TicketCount => _tickets.Count;

    public HitLApprovalResult ApproveStepUpRequest(string approvalId, string approverSid)
    {
        if (string.IsNullOrWhiteSpace(approverSid))
            throw new ArgumentException("Approver SID cannot be null or whitespace.", nameof(approverSid));

        return ApproveStepUpRequest(approvalId, new HitLApproverContext(approverSid, new[] { approverSid }, TenantId: null, IsCrossTenantAdmin: true));
    }

    public HitLApprovalResult ApproveStepUpRequest(string approvalId, HitLApproverContext approver)
    {
        if (string.IsNullOrWhiteSpace(approvalId))
            throw new ArgumentException("Approval ID cannot be null or whitespace.", nameof(approvalId));

        ArgumentNullException.ThrowIfNull(approver);

        if (string.IsNullOrWhiteSpace(approver.ApproverSid))
            throw new ArgumentException("Approver SID cannot be null or whitespace.", nameof(approver));

        if (!TryGetEntryForApprover(approvalId, approver, out var entry))
        {
            return NotFoundResult(approvalId);
        }

        IAsyncDisposable? distLock = null;
        if (_clusterState != null)
        {
            try
            {
                distLock = _clusterState.TryAcquireLockAsync($"hitl:lock:{approvalId}", TimeSpan.FromSeconds(5)).AsTask().GetAwaiter().GetResult();
                if (distLock == null)
                {
                    return new HitLApprovalResult(false, entry.Ticket, "Ticket is currently being decided on another cluster node. Please retry.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to acquire distributed lock for ticket '{ApprovalId}'.", approvalId);
            }
        }

        try
        {
            lock (entry.Lock)
            {
                // VULN-05: Replay & Race condition prevention
                if (entry.Ticket.Status != HitLApprovalStatus.Pending)
                {
                    return new HitLApprovalResult(
                        false,
                        entry.Ticket,
                        $"Ticket is already in status '{entry.Ticket.Status}'. Cannot approve."
                    );
                }

                // VULN-04 / SEC C-05: Self-Approval Bypass prevention (Four-Eyes invariant).
                // Every identifier of the approver is compared, so oid/sub/upn/PrimarySid variants of the same user are caught.
                if (_options.Value.HitLStepUp.RequireDifferentApprover && IsSameIdentity(entry.Ticket.RequesterSid, approver))
                {
                    _logger.LogWarning("Four-Eyes security violation: Requester '{RequesterSid}' attempted self-approval on ticket '{ApprovalId}'.",
                        entry.Ticket.RequesterSid, approvalId);

                    return new HitLApprovalResult(
                        false,
                        entry.Ticket,
                        "Four-Eyes security violation: Self-approval is strictly prohibited. Approver cannot be the requester."
                    );
                }

                entry.Ticket = entry.Ticket with
                {
                    Status = HitLApprovalStatus.Approved,
                    ApproverSid = approver.ApproverSid
                };
                entry.CompletedAt = DateTimeOffset.UtcNow;

                if (_clusterState != null)
                {
                    try
                    {
                        _clusterState.SetAsync($"hitl:ticket:{approvalId}", entry.Ticket, CompletedTicketRetention).AsTask().GetAwaiter().GetResult();
                        _clusterState.PublishEventAsync($"hitl:events:{approvalId}", new HitLApprovalBroadcast(approvalId, approver.ApproverSid, true)).AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to broadcast HitL approval for ticket '{ApprovalId}' to cluster.", approvalId);
                    }
                }

                var approvedResult = new HitLApprovalResult(true, entry.Ticket, "Approval granted.");
                entry.Tcs.TrySetResult(approvedResult);

                _logger.LogInformation("HitL ticket '{ApprovalId}' approved by '{ApproverSid}'.", approvalId, approver.ApproverSid);
                return approvedResult;
            }
        }
        finally
        {
            if (distLock != null)
            {
                try { distLock.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* ignore */ }
            }
        }
    }

    public HitLApprovalResult RejectStepUpRequest(string approvalId, string approverSid, string? reason = null)
    {
        IReadOnlyCollection<string> identifiers = string.IsNullOrWhiteSpace(approverSid) ? Array.Empty<string>() : new[] { approverSid };
        return RejectStepUpRequest(
            approvalId,
            new HitLApproverContext(approverSid, identifiers, TenantId: null, IsCrossTenantAdmin: true),
            reason);
    }

    public HitLApprovalResult RejectStepUpRequest(string approvalId, HitLApproverContext approver, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(approvalId))
            throw new ArgumentException("Approval ID cannot be null or whitespace.", nameof(approvalId));

        ArgumentNullException.ThrowIfNull(approver);

        if (!TryGetEntryForApprover(approvalId, approver, out var entry))
        {
            return NotFoundResult(approvalId);
        }

        IAsyncDisposable? distLock = null;
        if (_clusterState != null)
        {
            try
            {
                distLock = _clusterState.TryAcquireLockAsync($"hitl:lock:{approvalId}", TimeSpan.FromSeconds(5)).AsTask().GetAwaiter().GetResult();
                if (distLock == null)
                {
                    return new HitLApprovalResult(false, entry.Ticket, "Ticket is currently being decided on another cluster node. Please retry.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to acquire distributed lock for ticket '{ApprovalId}'.", approvalId);
            }
        }

        try
        {
            lock (entry.Lock)
            {
                if (entry.Ticket.Status != HitLApprovalStatus.Pending)
                {
                    return new HitLApprovalResult(
                        false,
                        entry.Ticket,
                        $"Ticket is already in status '{entry.Ticket.Status}'. Cannot reject."
                    );
                }

                entry.Ticket = entry.Ticket with
                {
                    Status = HitLApprovalStatus.Rejected,
                    ApproverSid = approver.ApproverSid,
                    RejectionReason = reason ?? "Rejected by data steward."
                };
                entry.CompletedAt = DateTimeOffset.UtcNow;

                if (_clusterState != null)
                {
                    try
                    {
                        _clusterState.SetAsync($"hitl:ticket:{approvalId}", entry.Ticket, CompletedTicketRetention).AsTask().GetAwaiter().GetResult();
                        _clusterState.PublishEventAsync($"hitl:events:{approvalId}", new HitLApprovalBroadcast(approvalId, approver.ApproverSid, false, entry.Ticket.RejectionReason)).AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to broadcast HitL rejection for ticket '{ApprovalId}' to cluster.", approvalId);
                    }
                }

                var rejectedResult = new HitLApprovalResult(false, entry.Ticket, entry.Ticket.RejectionReason);
                entry.Tcs.TrySetResult(rejectedResult);

                _logger.LogInformation("HitL ticket '{ApprovalId}' rejected by '{ApproverSid}'. Reason: {Reason}",
                    approvalId, approver.ApproverSid, entry.Ticket.RejectionReason);
                return rejectedResult;
            }
        }
        finally
        {
            if (distLock != null)
            {
                try { distLock.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// SEC C-05: Tickets of foreign tenants are reported as "not found" (no existence oracle) unless the approver is a cross-tenant admin.
    /// </summary>
    private bool TryGetEntryForApprover(string approvalId, HitLApproverContext approver, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TicketEntry? entry)
    {
        PurgeStaleTickets(DateTimeOffset.UtcNow);

        if (!_tickets.TryGetValue(approvalId, out entry))
        {
            if (_clusterState != null)
            {
                try
                {
                    var remoteTicket = _clusterState.GetAsync<HitLApprovalTicket>($"hitl:ticket:{approvalId}").AsTask().GetAwaiter().GetResult();
                    if (remoteTicket != null)
                    {
                        entry = _tickets.GetOrAdd(approvalId, _ => new TicketEntry(remoteTicket));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch remote HitL ticket '{ApprovalId}' from cluster state.", approvalId);
                }
            }
        }

        if (entry == null)
        {
            return false;
        }

        if (!approver.IsCrossTenantAdmin &&
            !string.Equals(entry.Ticket.TenantId, approver.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Cross-tenant HitL decision blocked: approver '{ApproverSid}' (tenant '{ApproverTenant}') attempted to act on ticket '{ApprovalId}' of another tenant.",
                approver.ApproverSid, approver.TenantId ?? "none", approvalId);
            entry = null;
            return false;
        }

        return true;
    }

    internal static bool IsSameIdentity(string requesterSid, HitLApproverContext approver)
    {
        if (string.IsNullOrWhiteSpace(requesterSid))
        {
            return false;
        }

        var requester = requesterSid.Trim();
        if (!string.IsNullOrWhiteSpace(approver.ApproverSid) &&
            string.Equals(requester, approver.ApproverSid.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var id in approver.Identifiers)
        {
            if (!string.IsNullOrWhiteSpace(id) && string.Equals(requester, id.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static HitLApprovalResult NotFoundResult(string approvalId)
    {
        return new HitLApprovalResult(
            false,
            new HitLApprovalTicket(approvalId, "Unknown", "Unknown", "Unknown", TableIdentifier.Parse("public.unknown"), null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, HitLApprovalStatus.Rejected),
            "Approval ticket not found."
        );
    }

    public HitLApprovalTicket? GetTicket(string approvalId)
    {
        if (_tickets.TryGetValue(approvalId, out var entry))
        {
            return entry.Ticket;
        }

        if (_clusterState != null)
        {
            try
            {
                return _clusterState.GetAsync<HitLApprovalTicket>($"hitl:ticket:{approvalId}").AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch remote HitL ticket '{ApprovalId}' from cluster state.", approvalId);
            }
        }

        return null;
    }

    public IReadOnlyList<HitLApprovalTicket> GetPendingTickets(string? tenantId = null)
    {
        PurgeStaleTickets(DateTimeOffset.UtcNow);

        var query = _tickets.Values.Select(e => e.Ticket).Where(t => t.Status == HitLApprovalStatus.Pending);
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            query = query.Where(t => string.Equals(t.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
        }

        return query.ToList();
    }
}
