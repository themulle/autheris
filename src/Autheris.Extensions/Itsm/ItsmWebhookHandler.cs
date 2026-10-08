namespace Autheris.Extensions.Itsm;

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed class ItsmStatusChangeDto
{
    public string TicketId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string Action { get; set; } = "APPROVE"; // "APPROVE", "REJECT"
    public string? Reason { get; set; }
    public string System { get; set; } = "ITSM";

    /// <summary>SEC H-06: Event/delivery id from the signed payload (replay protection).</summary>
    public string? EventId { get; set; }

    /// <summary>SEC H-06: Approver as reported by the ITSM system (audit actor).</summary>
    public string? Approver { get; set; }
}

/// <summary>
/// SEC H-06: In-memory replay cache for ITSM webhook deliveries (event id and signature), TTL-bound.
/// Covers the full +/- 5 minute timestamp tolerance window plus margin.
/// </summary>
public sealed class ItsmWebhookReplayCache
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>Process-wide default instance (the webhook handler itself is scoped).</summary>
    public static ItsmWebhookReplayCache Shared { get; } = new();

    internal int Count => _seen.Count;

    /// <summary>Registers a delivery key. Returns false if the key was already seen within the window (replay).</summary>
    public bool TryRegister(string key, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_seen.Count >= PruneThreshold)
        {
            Prune(now);
        }

        var expiresAt = now + Window;
        while (true)
        {
            if (_seen.TryAdd(key, expiresAt))
            {
                return true;
            }

            if (_seen.TryGetValue(key, out var existing))
            {
                if (existing > now)
                {
                    return false;
                }

                if (_seen.TryUpdate(key, expiresAt, existing))
                {
                    return true;
                }
            }
        }
    }

    public void Remove(string key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            _seen.TryRemove(key, out _);
        }
    }

    internal void Prune(DateTimeOffset now)
    {
        foreach (var kvp in _seen)
        {
            if (kvp.Value <= now)
            {
                _seen.TryRemove(kvp.Key, out _);
            }
        }
    }
}

public sealed class ItsmWebhookHandler(
    IKeyVaultSecretProvider secretProvider,
    IConsentApprovalRepository governanceRepo,
    IOptions<GatewayOptions> options,
    ILogger<ItsmWebhookHandler> logger,
    IEventBus? eventBus = null,
    ItsmWebhookReplayCache? replayCache = null) : IItsmWebhookHandler
{
    private const string GlobalWebhookSecretRef = "itsm:webhook-secret";
    private readonly ItsmOptions _itsmOptions = options.Value.Itsm;
    private readonly ItsmWebhookReplayCache _replayCache = replayCache ?? ItsmWebhookReplayCache.Shared;

    public Task<bool> HandleStatusChangeAsync(
        string rawPayload,
        string hmacSignature,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
        => HandleStatusChangeAsync(rawPayload, hmacSignature, timestamp, null, ct);

    public Task<bool> HandleStatusChangeAsync(
        string rawPayload,
        string hmacSignature,
        DateTimeOffset timestamp,
        string? headerInstanceId,
        CancellationToken ct = default)
        => HandleStatusChangeAsync(rawPayload, hmacSignature, timestamp, headerInstanceId, null, ct);

    public async Task<bool> HandleStatusChangeAsync(
        string rawPayload,
        string hmacSignature,
        DateTimeOffset timestamp,
        string? headerInstanceId,
        string? rawTimestampHeader,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPayload);

        bool bypassSignature = options.Value.IsWebhookSignatureBypassed;
        if (!bypassSignature && string.IsNullOrWhiteSpace(hmacSignature))
        {
            throw new ArgumentException("HMAC signature must be provided unless danger_bypass_webhook_signature_validation is enabled.", nameof(hmacSignature));
        }

        // 1. Replay-Schutz: 5 Minuten Gültigkeitsfenster (umgehbar via warn_ignore_webhook_timestamp_tolerance)
        bool ignoreTimestampTolerance = options.Value.IsWebhookTimestampToleranceIgnored;
        if (!ignoreTimestampTolerance)
        {
            var diff = DateTimeOffset.UtcNow - timestamp;
            if (diff > TimeSpan.FromMinutes(5) || diff < TimeSpan.FromMinutes(-5))
            {
                logger.LogWarning("Webhook rejected: Timestamp is outside the 5-minute validity window.");
                return false;
            }
        }

        // 2. Payload parsen (unterstützt kanonisches DTO, natives ServiceNow- und natives Jira-Format).
        // SEC H-06: Die Instanz-ID stammt ausschließlich aus dem (anschließend signaturgeprüften) Payload.
        var payload = ParsePayload(rawPayload, logger);
        if (payload == null || string.IsNullOrWhiteSpace(payload.TicketId))
        {
            logger.LogWarning("Webhook rejected: TicketId could not be determined.");
            return false;
        }

        var instanceId = ResolveInstanceId(payload.InstanceId, headerInstanceId, bypassSignature);
        if (instanceId == null)
        {
            return false;
        }

        payload.InstanceId = instanceId;

        // 3. Secret-Bezug & Signaturvergleich (umgehbar via danger_bypass_webhook_signature_validation)
        string? normalizedSignature = null;
        if (!bypassSignature)
        {
            // SEC H-06: Secret pro Instanz; das globale Secret nur bei explizitem LegacyGlobalWebhookSecret.
            var secretKey = ResolveWebhookSecret(instanceId);
            if (secretKey == null)
            {
                logger.LogError("Webhook rejected: No dedicated webhook secret (itsm:webhook-secret:<instanceId>) is configured for ITSM instance '{InstanceId}'.", instanceId);
                return false;
            }

            var cleanSig = hmacSignature.Trim();
            if (cleanSig.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            {
                cleanSig = cleanSig["sha256=".Length..];
            }

            byte[] providedHash;
            try
            {
                providedHash = Convert.FromHexString(cleanSig);
            }
            catch (FormatException)
            {
                logger.LogWarning("Webhook rejected: Invalid hex format of the HMAC-SHA256 signature.");
                return false;
            }

            // Support both raw header timestamp (e.g. Unix epoch or client formatting) and normalized ISO-8601
            bool signatureValid = false;
            if (!string.IsNullOrWhiteSpace(rawTimestampHeader))
            {
                byte[] computedHashWithRawTimestamp = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes($"t={rawTimestampHeader}.v1={rawPayload}"));
                signatureValid = CryptographicOperations.FixedTimeEquals(computedHashWithRawTimestamp, providedHash);
            }

            if (!signatureValid)
            {
                byte[] computedHashWithTimestamp = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes($"t={timestamp:O}.v1={rawPayload}"));
                signatureValid = CryptographicOperations.FixedTimeEquals(computedHashWithTimestamp, providedHash);
            }

            if (!signatureValid)
            {
                logger.LogWarning("Webhook rejected: Invalid HMAC-SHA256 signature.");
                return false;
            }

            normalizedSignature = Convert.ToHexString(providedHash);
        }
        else
        {
            logger.LogWarning("[INSECURE GETTING STARTED] Bypassing ITSM webhook HMAC-SHA256 signature verification.");
        }

        // 4. SEC H-06: Replay-Schutz über Event-/Delivery-ID und Signatur (TTL-Cache). Wiederholte Zustellungen
        // werden idempotent quittiert, ohne erneut eine Statusänderung auszulösen.
        var now = DateTimeOffset.UtcNow;
        var replayKeys = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(payload.EventId))
        {
            replayKeys.Add($"evt:{instanceId}:{payload.EventId}");
        }

        if (normalizedSignature != null)
        {
            replayKeys.Add($"sig:{instanceId}:{normalizedSignature}");
        }

        var registeredKeys = new List<string>(replayKeys.Count);
        foreach (var key in replayKeys)
        {
            if (!_replayCache.TryRegister(key, now))
            {
                foreach (var registered in registeredKeys)
                {
                    _replayCache.Remove(registered);
                }

                logger.LogWarning(
                    "Webhook replay detected (instance '{InstanceId}', ticket '{TicketId}'). Delivery is acknowledged without effect.",
                    SanitizeForLog(instanceId),
                    SanitizeForLog(payload.TicketId));
                return true;
            }

            registeredKeys.Add(key);
        }

        var processed = false;
        try
        {
            processed = await ProcessStatusChangeAsync(payload, instanceId, ct).ConfigureAwait(false);
            return processed;
        }
        finally
        {
            if (!processed)
            {
                // Only deliveries that actually took effect are remembered; failed ones may be retried.
                foreach (var registered in registeredKeys)
                {
                    _replayCache.Remove(registered);
                }
            }
        }
    }

    private async Task<bool> ProcessStatusChangeAsync(ItsmStatusChangeDto payload, string instanceId, CancellationToken ct)
    {
        // Sanitize untrusted data before writing it to logs (prevent log forging via CR/LF).
        var logInstanceId = instanceId.Replace("\r", string.Empty).Replace("\n", string.Empty);

        // 5. Strikte Tenant-Bindung (umgehbar via warn_fallback_default_tenant_for_webhooks).
        // SEC H-06: Ticket-Lookup erfolgt mit Tenant-Filter (WHERE itsm_ticket_id = @t AND tenant_id = @tenant).
        var expectedTenant = _itsmOptions.GetTenantForInstance(instanceId);
        ConsentRequest? request;
        if (expectedTenant != null)
        {
            request = await governanceRepo.GetConsentRequestByTicketIdAsync(payload.TicketId, expectedTenant.Value, ct).ConfigureAwait(false);
            if (request == null && options.Value.IsWebhookTenantFallbackAllowed)
            {
                request = await governanceRepo.GetConsentRequestByTicketIdAsync(payload.TicketId, ct).ConfigureAwait(false);
                if (request != null)
                {
                    logger.LogWarning(
                        "[DANGER] Bypassing cross-tenant mismatch for ticket {TicketId}. Request tenant: {ReqTenant}, callback instance: {InstanceId}. Prohibited outside Development.",
                        payload.TicketId, request.TenantId, logInstanceId);
                }
            }
        }
        else if (options.Value.IsWebhookTenantFallbackAllowed)
        {
            request = await governanceRepo.GetConsentRequestByTicketIdAsync(payload.TicketId, ct).ConfigureAwait(false);
            if (request != null)
            {
                logger.LogWarning(
                    "[DANGER] Bypassing tenant binding for unmapped ITSM instance {InstanceId} (ticket {TicketId}, request tenant {ReqTenant}). Prohibited outside Development.",
                    logInstanceId, payload.TicketId, request.TenantId);
            }
        }
        else
        {
            GatewayDiagnostics.CrossTenantMismatchCounter.Add(1);
            logger.LogError(
                "CROSS_TENANT_WEBHOOK_MISMATCH: Callback for ticket {TicketId} came from an unassigned instance {InstanceId}.",
                payload.TicketId, logInstanceId);
            return false; // Streng verweigern!
        }

        if (request == null)
        {
            logger.LogWarning("Webhook discarded: Unknown TicketId '{TicketId}' for instance '{InstanceId}'.", payload.TicketId, logInstanceId);
            return false;
        }

        // 6. Idempotente Bearbeitung (nur PENDING_EXTERNAL_APPROVAL darf bearbeitet werden)
        if (!string.Equals(request.Status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Webhook ignoriert: Request befindet sich bereits im Status '{Status}'", request.Status);
            return true;
        }

        // SEC H-06: Audit-Actor ist die ITSM-Instanz bzw. der dort gemeldete Genehmiger, nicht der Antragsteller.
        var actor = BuildActorSid(payload, instanceId);

        if (string.Equals(payload.Action, "REJECT", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Consent Request {RequestId} via ITSM Ticket {TicketId} ({System}) abgelehnt.", request.Id, payload.TicketId, payload.System);
            await governanceRepo.RejectConsentRequestAsync(
                request.Id,
                actor,
                payload.Reason ?? $"Rejected via {payload.System} webhook",
                isExternalItsm: true,
                ct).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(payload.Action, "APPROVE", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Consent Request {RequestId} via ITSM Ticket {TicketId} ({System}) approved. Running approval step...", request.Id, payload.TicketId, payload.System);

            // SEC M-2: Route approval through ApproveConsentRequestStepAsync to enforce separation of duties,
            // approver != requester check, and four-eyes verification rather than bypassing directly to ActivateConsentAsync.
            // Review R3-1: the approver is passed typed so the repository can check it against the table owners.
            var stepResult = await governanceRepo.ApproveConsentRequestStepAsync(
                request.Id,
                actor,
                isExternalItsmApproval: true,
                itsmApproverAccount: string.IsNullOrWhiteSpace(payload.Approver) ? null : payload.Approver.Trim(),
                ct).ConfigureAwait(false);

            if (string.Equals(stepResult.Status, "APPROVED", StringComparison.OrdinalIgnoreCase))
            {
                await governanceRepo.ActivateConsentAsync(request.Id, actor, ct).ConfigureAwait(false);

                if (eventBus != null)
                {
                    await eventBus.PublishAsync($"governance:policy-epoch-increment:{request.TenantId.Value}", request.TenantId.Value, ct).ConfigureAwait(false);
                }
            }

            return true;
        }

        if (string.Equals(payload.Action, "IGNORE", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Webhook ignoriert: Ticket-Status ergibt weder Genehmigung noch Ablehnung (Ticket {TicketId}).", payload.TicketId);
            return true;
        }

        logger.LogWarning("Webhook ignoriert: Unbekannte Action '{Action}'", payload.Action);
        return false;
    }

    /// <summary>
    /// SEC H-06: The instance comes from the signed payload only. An (unsigned) header instance must match it.
    /// Only in signature-bypass mode (insecure getting started) the header is accepted as fallback.
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sanitized = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '\r' || ch == '\n')
            {
                sanitized.Append(' ');
                continue;
            }

            sanitized.Append(char.IsControl(ch) ? ' ' : ch);
        }

        return sanitized.ToString();
    }

    private string? ResolveInstanceId(string? payloadInstanceId, string? headerInstanceId, bool bypassSignature)
    {
        var fromPayload = string.IsNullOrWhiteSpace(payloadInstanceId) ? null : payloadInstanceId.Trim();
        var fromHeader = string.IsNullOrWhiteSpace(headerInstanceId) ? null : headerInstanceId.Trim();

        if (fromPayload != null && fromHeader != null &&
            !string.Equals(fromPayload, fromHeader, StringComparison.OrdinalIgnoreCase))
        {
            GatewayDiagnostics.CrossTenantMismatchCounter.Add(1);
            logger.LogWarning(
                "Webhook abgelehnt: Instanz-Header '{HeaderInstance}' widerspricht der signierten Payload-Instanz '{PayloadInstance}'.",
                SanitizeForLog(fromHeader),
                SanitizeForLog(fromPayload));
            return null;
        }

        if (fromPayload != null)
        {
            return fromPayload;
        }

        if (bypassSignature && fromHeader != null)
        {
            return fromHeader;
        }

        logger.LogWarning("Webhook rejected: The signed payload does not contain an ITSM instance ID.");
        return null;
    }

    /// <summary>
    /// SEC H-06: Resolves the per-instance webhook secret. Values that are only aliases of the global secret
    /// or the development placeholder (secret reference used as key) are not accepted as instance secret.
    /// </summary>
    internal byte[]? ResolveWebhookSecret(string instanceId)
    {
        var instanceRef = $"{GlobalWebhookSecretRef}:{instanceId}";
        var instanceSecret = TryGetSecret(instanceRef);
        var globalSecret = TryGetSecret(GlobalWebhookSecretRef);

        if (instanceSecret is { Length: > 0 } &&
            !IsPlaceholder(instanceSecret, instanceRef) &&
            (globalSecret == null || !CryptographicOperations.FixedTimeEquals(instanceSecret, globalSecret)))
        {
            return instanceSecret;
        }

        if (_itsmOptions.LegacyGlobalWebhookSecret && globalSecret is { Length: > 0 } && !IsPlaceholder(globalSecret, GlobalWebhookSecretRef))
        {
            logger.LogWarning("[DANGER] ITSM webhook for instance '{InstanceId}' verified with the legacy global secret (Itsm:LegacyGlobalWebhookSecret=true). Prohibited outside Development.", instanceId);
            return globalSecret;
        }

        return null;
    }

    private byte[]? TryGetSecret(string secretRef)
    {
        try
        {
            var bytes = secretProvider.GetSecretBytes(secretRef);
            return bytes is { Length: > 0 } ? bytes : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Webhook secret '{SecretRef}' could not be loaded.", secretRef);
            return null;
        }
    }

    private static bool IsPlaceholder(byte[] secret, string secretRef)
        => CryptographicOperations.FixedTimeEquals(secret, Encoding.UTF8.GetBytes(secretRef));

    private static Sid BuildActorSid(ItsmStatusChangeDto payload, string instanceId)
    {
        var system = $"ITSM_{payload.System.ToUpperInvariant()}";
        return string.IsNullOrWhiteSpace(payload.Approver)
            ? new Sid($"{system}:{instanceId}")
            : new Sid($"{system}:{instanceId}:{payload.Approver.Trim()}");
    }

    private static string? TryGetStringProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop))
            {
                var value = prop.ValueKind switch
                {
                    JsonValueKind.String => prop.GetString(),
                    JsonValueKind.Number => prop.GetRawText(),
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static ItsmStatusChangeDto? ParsePayload(string rawPayload, ILogger logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            var root = doc.RootElement;

            string ticketId = string.Empty;
            string instanceId = string.Empty;
            string action = "IGNORE"; // Review G5: unknown states are ignored, never treated as rejection
            string? reason = null;
            string detectedSystem = "ITSM";

            // A. Kanonische DTO-Felder
            if (root.TryGetProperty("TicketId", out var tProp) || root.TryGetProperty("ticketId", out tProp) || root.TryGetProperty("ticket_id", out tProp))
            {
                ticketId = tProp.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(instanceId))
            {
                if (root.TryGetProperty("InstanceId", out var iProp) || root.TryGetProperty("instanceId", out iProp) || root.TryGetProperty("instance_id", out iProp))
                {
                    instanceId = iProp.GetString() ?? string.Empty;
                }
            }

            if (root.TryGetProperty("Action", out var aProp) || root.TryGetProperty("action", out aProp))
            {
                action = aProp.GetString() ?? "IGNORE";
            }

            if (root.TryGetProperty("Reason", out var rProp) || root.TryGetProperty("reason", out rProp))
            {
                reason = rProp.GetString();
            }

            // B. Natives ServiceNow-Payload Format (number, sys_id, approval, state, close_notes)
            if (string.IsNullOrWhiteSpace(ticketId))
            {
                if (root.TryGetProperty("number", out var numProp))
                {
                    ticketId = numProp.GetString() ?? string.Empty;
                    detectedSystem = "ServiceNow";
                }
                else if (root.TryGetProperty("sys_id", out var sysProp))
                {
                    ticketId = sysProp.GetString() ?? string.Empty;
                    detectedSystem = "ServiceNow";
                }

                if (detectedSystem == "ServiceNow")
                {
                    if (string.IsNullOrWhiteSpace(instanceId))
                    {
                        if (root.TryGetProperty("instance_name", out var inProp) || root.TryGetProperty("instance_id", out inProp))
                        {
                            instanceId = inProp.GetString() ?? string.Empty;
                        }
                    }

                    if (root.TryGetProperty("approval", out var appProp))
                    {
                        var app = appProp.GetString() ?? string.Empty;
                        if (string.Equals(app, "rejected", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(app, "not_approved", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "REJECT";
                        }
                        else if (string.Equals(app, "approved", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "APPROVE";
                        }
                    }
                    else if (root.TryGetProperty("state", out var stateProp))
                    {
                        var stateStr = stateProp.GetString() ?? stateProp.ToString();
                        if (stateStr == "4" || stateStr == "7" ||
                            string.Equals(stateStr, "rejected", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(stateStr, "closed_incomplete", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(stateStr, "cancelled", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "REJECT";
                        }
                        else if (string.Equals(stateStr, "approved", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "APPROVE";
                        }
                    }

                    if (reason == null)
                    {
                        if (root.TryGetProperty("close_notes", out var cnProp))
                        {
                            reason = cnProp.GetString();
                        }
                        else if (root.TryGetProperty("work_notes", out var wnProp))
                        {
                            reason = wnProp.GetString();
                        }
                        else if (root.TryGetProperty("comments", out var cProp))
                        {
                            reason = cProp.GetString();
                        }
                    }
                }
            }

            // C. Natives Jira-Payload Format (issue.key, issue.fields.status.name, resolution)
            if (string.IsNullOrWhiteSpace(ticketId) && root.TryGetProperty("issue", out var issueProp))
            {
                detectedSystem = "Jira";
                if (issueProp.TryGetProperty("key", out var keyProp))
                {
                    ticketId = keyProp.GetString() ?? string.Empty;
                }

                if (issueProp.TryGetProperty("fields", out var fieldsProp))
                {
                    string? resolutionName = null;
                    if (fieldsProp.TryGetProperty("resolution", out var resProp) &&
                        resProp.TryGetProperty("name", out var resNameProp))
                    {
                        resolutionName = resNameProp.GetString();
                        if (reason == null) reason = resolutionName;
                    }

                    if (fieldsProp.TryGetProperty("status", out var statusProp) &&
                        statusProp.TryGetProperty("name", out var statusNameProp))
                    {
                        var statusName = statusNameProp.GetString() ?? string.Empty;
                        bool isResolutionRejected = resolutionName != null &&
                            (resolutionName.Contains("Won't", StringComparison.OrdinalIgnoreCase) ||
                             resolutionName.Equals("Declined", StringComparison.OrdinalIgnoreCase) ||
                             resolutionName.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                             resolutionName.Equals("Rejected", StringComparison.OrdinalIgnoreCase));

                        if (statusName.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
                            statusName.Equals("Declined", StringComparison.OrdinalIgnoreCase) ||
                            statusName.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                            statusName.Contains("Won't", StringComparison.OrdinalIgnoreCase) ||
                            isResolutionRejected)
                        {
                            action = "REJECT";
                        }
                        else if (statusName.Equals("Approved", StringComparison.OrdinalIgnoreCase) ||
                                 statusName.Equals("Authorized", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(resolutionName, "Approved", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "APPROVE";
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    if (root.TryGetProperty("baseUrl", out var baseProp))
                    {
                        instanceId = baseProp.GetString() ?? string.Empty;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(ticketId))
            {
                logger.LogWarning("Webhook rejected: TicketId could not be determined from the DTO or from the ServiceNow or Jira structure.");
                return null;
            }

            var eventId = TryGetStringProperty(root, "EventId", "eventId", "event_id", "DeliveryId", "deliveryId", "delivery_id");
            // Review R4-4: only an explicit approver field counts. "sys_updated_by" (ServiceNow: last editor) and the Jira
            // "user" object (the event trigger) identify whoever touched the ticket last, not who approved it, and must
            // never turn a later editor into a second approver. Without an explicit approver, tables with configured
            // owners refuse the approval (fail-closed).
            var approver = TryGetStringProperty(root, "Approver", "approver", "ApprovedBy", "approvedBy", "approved_by");

            return new ItsmStatusChangeDto
            {
                TicketId = ticketId,
                InstanceId = instanceId,
                Action = action,
                Reason = reason,
                System = detectedSystem,
                EventId = eventId,
                Approver = approver
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Webhook rejected: Invalid JSON payload.");
            return null;
        }
    }
}
