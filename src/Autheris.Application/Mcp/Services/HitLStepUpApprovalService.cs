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
    private readonly Autheris.Application.Security.Totp.Interfaces.ITotpVerificationService? _totpService;
    private readonly Autheris.Application.Security.Totp.Interfaces.ITotpSecretStore? _totpSecretStore;
    private readonly byte[] _hmacKey;
    private static readonly byte[] ProcessFallbackHmacKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private static byte[] ResolveHitLHmacKey(GatewayOptions? options)
    {
        var rawKey = options?.DataMasking?.HmacSecretKeyVaultRef;
        if (!string.IsNullOrWhiteSpace(rawKey) && rawKey != "DEV_INSECURE_TEST_KEY_ONLY")
        {
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(rawKey);
            return System.Security.Cryptography.HKDF.DeriveKey(
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                keyBytes,
                32,
                info: "Autheris:HitLStepUp:v1"u8.ToArray());
        }

        return ProcessFallbackHmacKey;
    }

    internal static string ComputeTicketSignature(byte[] key, HitLApprovalTicket ticket)
    {
        var payload = $"{ticket.ApprovalId}:{ticket.ToolName}:{ticket.TenantId}:{ticket.RequesterSid}:{ticket.TargetTable}:{ticket.Status}:{ticket.ApproverSid}:{ticket.CreatedAt.ToUnixTimeSeconds()}:{ticket.ExpiresAt.ToUnixTimeSeconds()}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(key);
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    internal static string ComputeBroadcastSignature(byte[] key, HitLApprovalBroadcast broadcast)
    {
        var payload = $"{broadcast.ApprovalId}:{broadcast.ApproverSid}:{broadcast.IsApproved}:{broadcast.Reason}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(key);
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    internal static bool VerifyTicketSignature(byte[] key, HitLApprovalTicket ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket.Signature)) return false;
        var expected = ComputeTicketSignature(key, ticket);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(ticket.Signature));
    }

    internal static bool VerifyBroadcastSignature(byte[] key, HitLApprovalBroadcast broadcast)
    {
        if (string.IsNullOrWhiteSpace(broadcast.Signature)) return false;
        var expected = ComputeBroadcastSignature(key, broadcast);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(broadcast.Signature));
    }

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
        Autheris.Application.State.IDistributedClusterStateProvider? clusterState = null,
        Autheris.Application.Security.Totp.Interfaces.ITotpVerificationService? totpVerificationService = null,
        Autheris.Application.Security.Totp.Interfaces.ITotpSecretStore? totpSecretStore = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scopeFactory = scopeFactory;
        _clusterState = clusterState;
        _totpService = totpVerificationService;
        _totpSecretStore = totpSecretStore;
        _hmacKey = ResolveHitLHmacKey(options.Value);
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
                        var safeTicketId = (itsmTicketId ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
                        var safeApprovalIdForLog = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                        _logger.LogInformation("Dispatched HitL ITSM ticket '{TicketId}' for approval '{ApprovalId}'.", safeTicketId, safeApprovalIdForLog);
                    }
                }
            }
            catch (Exception ex)
            {
                var safeApprovalIdForLog = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                _logger.LogWarning(ex, "Failed to auto-create ITSM ticket for HitL approval '{ApprovalId}'. Proceeding with in-memory approval gate.", safeApprovalIdForLog);
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
        ticket = ticket with { Signature = ComputeTicketSignature(_hmacKey, ticket) };

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
                    if (!VerifyBroadcastSignature(_hmacKey, broadcast))
                    {
                        var safeApprovalIdForLog = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                        _logger.LogWarning("Dropping unverified or tampered HitL broadcast for ticket '{ApprovalId}'.", safeApprovalIdForLog);
                        return ValueTask.CompletedTask;
                    }

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
                var safeApprovalIdForLog = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                _logger.LogWarning(ex, "Failed to register HitL ticket '{ApprovalId}' in cluster state.", safeApprovalIdForLog);
            }
        }

        var safeRequesterSidForLog = requesterSid.Replace("\r", string.Empty).Replace("\n", string.Empty);
        var safeReqApprovalId = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
        _logger.LogInformation("HitL Step-Up approval requested. ID: {ApprovalId}, Table: {Table}, Requester: {RequesterSid}, Timeout: {Timeout}s",
            safeReqApprovalId, targetTable, safeRequesterSidForLog, timeoutSeconds);

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

                var safeTimeoutApprovalId = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                _logger.LogWarning("HitL Step-Up approval '{ApprovalId}' timed out after {Timeout}s. Failing closed.", safeTimeoutApprovalId, timeoutSeconds);
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

    public Task<HitLApprovalResult> ApproveStepUpRequestAsync(string approvalId, string approverSid, CancellationToken ct = default)
    {
        return ApproveStepUpRequestAsync(approvalId, approverSid, totpCode: null, ct);
    }

    public Task<HitLApprovalResult> ApproveStepUpRequestAsync(string approvalId, string approverSid, string? totpCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(approverSid))
            throw new ArgumentException("Approver SID cannot be null or whitespace.", nameof(approverSid));

        return ApproveStepUpRequestAsync(approvalId, new HitLApproverContext(approverSid, new[] { approverSid }, TenantId: null, IsCrossTenantAdmin: true), totpCode, ct);
    }

    public Task<HitLApprovalResult> ApproveStepUpRequestAsync(string approvalId, HitLApproverContext approver, CancellationToken ct = default)
    {
        return ApproveStepUpRequestAsync(approvalId, approver, totpCode: null, ct);
    }

    /// <summary>
    /// MCP-2: the decision itself happens under the ticket lock without I/O; the distributed lock, the cluster write
    /// and the broadcast are awaited outside of it (no sync-over-async, no Redis call inside a lock).
    /// ADR-05 / R-62: verifies RFC 6238 TOTP step-up authentication when configured or provided.
    /// </summary>
    public async Task<HitLApprovalResult> ApproveStepUpRequestAsync(string approvalId, HitLApproverContext approver, string? totpCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(approvalId))
            throw new ArgumentException("Approval ID cannot be null or whitespace.", nameof(approvalId));

        ArgumentNullException.ThrowIfNull(approver);

        if (string.IsNullOrWhiteSpace(approver.ApproverSid))
            throw new ArgumentException("Approver SID cannot be null or whitespace.", nameof(approver));

        var entry = await GetEntryForApproverAsync(approvalId, approver, ct).ConfigureAwait(false);
        if (entry == null)
        {
            return NotFoundResult(approvalId);
        }

        var (acquired, distLock) = await AcquireDistributedLockAsync(approvalId, ct).ConfigureAwait(false);
        if (!acquired)
        {
            return new HitLApprovalResult(false, entry.Ticket, "Ticket is currently being decided on another cluster node. Please retry.");
        }

        try
        {
            // Step-Up 2FA Validation (RFC 6238 TOTP, ADR-05, R-62)
            if (_totpService != null)
            {
                var isTotpEnforced = _options.Value.HitLStepUp.RequireTotp2Fa;
                var enrolledSecret = _totpSecretStore != null
                    ? await _totpSecretStore.GetSecretAsync(approver.ApproverSid, ct).ConfigureAwait(false)
                    : null;

                if (isTotpEnforced || !string.IsNullOrWhiteSpace(enrolledSecret) || !string.IsNullOrWhiteSpace(totpCode))
                {
                    if (string.IsNullOrWhiteSpace(totpCode))
                    {
                        return new HitLApprovalResult(
                            false,
                            entry.Ticket,
                            "TOTP 2FA code is required for Step-Up approval.");
                    }

                    if (string.IsNullOrWhiteSpace(enrolledSecret))
                    {
                        return new HitLApprovalResult(
                            false,
                            entry.Ticket,
                            "Approver has not enrolled in TOTP 2FA or secret is missing.");
                    }

                    var isTotpValid = await _totpService.VerifyAndConsumeTotpAsync(
                        approver.ApproverSid,
                        enrolledSecret,
                        totpCode,
                        ct).ConfigureAwait(false);

                    if (!isTotpValid)
                    {
                        var safeTotpApproverSid = (approver.ApproverSid ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
                        var safeTotpApprovalId = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                        _logger.LogWarning("TOTP verification failed for approver '{ApproverSid}' on ticket '{ApprovalId}'.",
                            safeTotpApproverSid, safeTotpApprovalId);

                        return new HitLApprovalResult(
                            false,
                            entry.Ticket,
                            "TOTP 2FA verification failed or one-time code was already used.");
                    }
                }
            }
            HitLApprovalResult approvedResult;
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
                    var safeRequesterSidForLog = (entry.Ticket.RequesterSid ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
                    var safeApprovalIdForWarn = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                    _logger.LogWarning("Four-Eyes security violation: Requester '{RequesterSid}' attempted self-approval on ticket '{ApprovalId}'.",
                        safeRequesterSidForLog, safeApprovalIdForWarn);

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

                approvedResult = new HitLApprovalResult(true, entry.Ticket, "Approval granted.");
                entry.Tcs.TrySetResult(approvedResult);
            }

            await BroadcastDecisionAsync(approvalId, approvedResult.Ticket, new HitLApprovalBroadcast(approvalId, approver.ApproverSid, true), ct).ConfigureAwait(false);
            var safeApprovalIdForLog = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
            var safeApproverSidForLog = (approver.ApproverSid ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
            _logger.LogInformation("HitL ticket '{ApprovalId}' approved by '{ApproverSid}'.", safeApprovalIdForLog, safeApproverSidForLog);
            return approvedResult;
        }
        finally
        {
            await ReleaseDistributedLockAsync(distLock).ConfigureAwait(false);
        }
    }

    public Task<HitLApprovalResult> RejectStepUpRequestAsync(string approvalId, string approverSid, string? reason = null, CancellationToken ct = default)
    {
        IReadOnlyCollection<string> identifiers = string.IsNullOrWhiteSpace(approverSid) ? Array.Empty<string>() : new[] { approverSid };
        return RejectStepUpRequestAsync(
            approvalId,
            new HitLApproverContext(approverSid, identifiers, TenantId: null, IsCrossTenantAdmin: true),
            reason,
            ct);
    }

    public async Task<HitLApprovalResult> RejectStepUpRequestAsync(string approvalId, HitLApproverContext approver, string? reason = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(approvalId))
            throw new ArgumentException("Approval ID cannot be null or whitespace.", nameof(approvalId));

        ArgumentNullException.ThrowIfNull(approver);

        var entry = await GetEntryForApproverAsync(approvalId, approver, ct).ConfigureAwait(false);
        if (entry == null)
        {
            return NotFoundResult(approvalId);
        }

        var (acquired, distLock) = await AcquireDistributedLockAsync(approvalId, ct).ConfigureAwait(false);
        if (!acquired)
        {
            return new HitLApprovalResult(false, entry.Ticket, "Ticket is currently being decided on another cluster node. Please retry.");
        }

        try
        {
            HitLApprovalResult rejectedResult;
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

                rejectedResult = new HitLApprovalResult(false, entry.Ticket, entry.Ticket.RejectionReason);
                entry.Tcs.TrySetResult(rejectedResult);
            }

            await BroadcastDecisionAsync(approvalId, rejectedResult.Ticket,
                new HitLApprovalBroadcast(approvalId, approver.ApproverSid, false, rejectedResult.Ticket.RejectionReason), ct).ConfigureAwait(false);
            var safeRejectApprovalIdForLog = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
            var safeRejectApproverSidForLog = (approver.ApproverSid ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
            var safeRejectReasonForLog = (rejectedResult.Ticket.RejectionReason ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
            _logger.LogInformation("HitL ticket '{ApprovalId}' rejected by '{ApproverSid}'. Reason: {Reason}",
                safeRejectApprovalIdForLog, safeRejectApproverSidForLog, safeRejectReasonForLog);
            return rejectedResult;
        }
        finally
        {
            await ReleaseDistributedLockAsync(distLock).ConfigureAwait(false);
        }
    }

    /// <summary>Acquired is false only when another node holds the lock; an unavailable cluster state does not block.</summary>
    private async Task<(bool Acquired, IAsyncDisposable? Lock)> AcquireDistributedLockAsync(string approvalId, CancellationToken ct)
    {
        if (_clusterState == null)
        {
            return (true, null);
        }

        try
        {
            var distLock = await _clusterState.TryAcquireLockAsync($"hitl:lock:{approvalId}", TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return (distLock != null, distLock);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var safeLockApprovalId = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
            _logger.LogWarning(ex, "Failed to acquire distributed lock for ticket '{ApprovalId}'.", safeLockApprovalId);
            return (true, null);
        }
    }

    private static async Task ReleaseDistributedLockAsync(IAsyncDisposable? distLock)
    {
        if (distLock == null)
        {
            return;
        }

        try
        {
            await distLock.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private async Task BroadcastDecisionAsync(string approvalId, HitLApprovalTicket ticket, HitLApprovalBroadcast broadcast, CancellationToken ct)
    {
        if (_clusterState == null)
        {
            return;
        }

        try
        {
            var signedTicket = ticket with { Signature = ComputeTicketSignature(_hmacKey, ticket) };
            var signedBroadcast = broadcast with { Signature = ComputeBroadcastSignature(_hmacKey, broadcast) };
            await _clusterState.SetAsync($"hitl:ticket:{approvalId}", signedTicket, CompletedTicketRetention, ct).ConfigureAwait(false);
            await _clusterState.PublishEventAsync($"hitl:events:{approvalId}", signedBroadcast, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var safeBroadcastApprovalId = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
            _logger.LogWarning(ex, "Failed to broadcast HitL decision for ticket '{ApprovalId}' to cluster.", safeBroadcastApprovalId);
        }
    }

    /// <summary>
    /// SEC C-05: Tickets of foreign tenants are reported as "not found" (no existence oracle) unless the approver is a cross-tenant admin.
    /// </summary>
    private async Task<TicketEntry?> GetEntryForApproverAsync(string approvalId, HitLApproverContext approver, CancellationToken ct)
    {
        PurgeStaleTickets(DateTimeOffset.UtcNow);

        if (!_tickets.TryGetValue(approvalId, out var entry))
        {
            if (_clusterState != null)
            {
                try
                {
                    var remoteTicket = await _clusterState.GetAsync<HitLApprovalTicket>($"hitl:ticket:{approvalId}", ct).ConfigureAwait(false);
                    if (remoteTicket != null)
                    {
                        if (!VerifyTicketSignature(_hmacKey, remoteTicket))
                        {
                            var safeFetchApprovalId = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                            _logger.LogWarning("Dropping unverified or tampered remote HitL ticket '{ApprovalId}' from cluster state.", safeFetchApprovalId);
                        }
                        else
                        {
                            entry = _tickets.GetOrAdd(approvalId, _ => new TicketEntry(remoteTicket));
                        }
                    }
                }
                catch (Exception ex)
                {
                    var safeFetchApprovalId = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                    _logger.LogWarning(ex, "Failed to fetch remote HitL ticket '{ApprovalId}' from cluster state.", safeFetchApprovalId);
                }
            }
        }

        if (entry == null)
        {
            return null;
        }

        if (!approver.IsCrossTenantAdmin &&
            !string.Equals(entry.Ticket.TenantId, approver.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            var safeApproverSidForLog = (approver.ApproverSid ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
            var safeTenantForLog = (approver.TenantId ?? "none").Replace("\r", string.Empty).Replace("\n", string.Empty);
            var safeBlockedApprovalId = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
            _logger.LogWarning("Cross-tenant HitL decision blocked: approver '{ApproverSid}' (tenant '{ApproverTenant}') attempted to act on ticket '{ApprovalId}' of another tenant.",
                safeApproverSidForLog, safeTenantForLog, safeBlockedApprovalId);
            return null;
        }

        return entry;
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

    public async Task<HitLApprovalTicket?> GetTicketAsync(string approvalId, CancellationToken ct = default)
    {
        if (_tickets.TryGetValue(approvalId, out var entry))
        {
            return entry.Ticket;
        }

        if (_clusterState != null)
        {
            try
            {
                return await _clusterState.GetAsync<HitLApprovalTicket>($"hitl:ticket:{approvalId}", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var safeGetTicketApprovalId = approvalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                _logger.LogWarning(ex, "Failed to fetch remote HitL ticket '{ApprovalId}' from cluster state.", safeGetTicketApprovalId);
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
