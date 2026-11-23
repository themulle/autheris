namespace Autheris.Application.ConsentRequests;

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;

/// <summary>
/// Fail-closed evidence validation for external workflow decisions: system and reference binding, an HMAC over the
/// request id, version, decision and approver bound to a timestamp and a single-use nonce, replay rejection, and mapping of
/// the approver to an Autheris principal through the identity realm (never by e-mail). It does not evaluate eligibility or
/// separation of duties: the engine does that for the mapped principal. A missing secret, replay store or mapping fails closed.
/// </summary>
public sealed class ExternalDecisionGuard(
    IExternalApproverResolver? approverResolver = null,
    IWebhookSecretProtector? secretProtector = null,
    IIdempotencyStore? replayStore = null,
    TimeProvider? timeProvider = null) : IExternalDecisionGuard
{
    public const string InvalidEvidenceCode = "EXTERNAL_EVIDENCE_INVALID";
    public const string ReplayCode = "EXTERNAL_EVIDENCE_REPLAY";
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReplayRetention = TimeSpan.FromHours(24);
    private const int MaxNonceLength = 128;
    private const string SignatureContext = "autheris-external-decision-v1";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Canonical MAC over the evidence fields. Exposed so transports and tests sign exactly what the guard verifies.</summary>
    public static string ComputeSignature(string secret, ExternalDecisionEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(envelope);
        var canonical = string.Join(
            '\n',
            SignatureContext,
            envelope.RequestId.ToString("D"),
            envelope.ExpectedVersion.ToString(CultureInfo.InvariantCulture),
            envelope.ExternalSystemId.ToString("D"),
            envelope.ExternalReference,
            envelope.Approve ? "APPROVE" : "REJECT",
            envelope.Approver.Subject,
            envelope.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            envelope.Nonce);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(mac);
    }

    public async Task<ValidatedExternalDecision> ValidateAsync(
        ExternalDecisionEnvelope envelope,
        ConsentRequest request,
        ConsentWorkflowRoute? route,
        ExternalWorkflowSystem? system,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(request);

        if (request.WorkflowMode != ConsentWorkflowMode.External)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.WorkflowModeMismatch,
                "The request uses the internal workflow and cannot be decided externally.",
                409);
        }

        if (envelope.RequestId != request.Id || envelope.ExpectedVersion <= 0 || string.IsNullOrWhiteSpace(envelope.ExternalReference))
        {
            throw Invalid("External evidence does not match the request.");
        }

        ValidateSystemBinding(envelope, request, route, system);

        var now = _time.GetUtcNow();
        if (envelope.Timestamp > now + MaxClockSkew || envelope.Timestamp < now - MaxClockSkew)
        {
            throw Invalid("External evidence is outside the accepted time window.");
        }

        if (string.IsNullOrWhiteSpace(envelope.Nonce) || envelope.Nonce.Length > MaxNonceLength ||
            string.IsNullOrWhiteSpace(envelope.Signature) || string.IsNullOrWhiteSpace(envelope.Approver?.Subject))
        {
            throw Invalid("External evidence is incomplete.");
        }

        var secret = ResolveSecret(system!)
            ?? throw new ConsentDecisionException(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized, "External evidence cannot be verified.", 403);
        VerifySignature(secret, envelope);

        // The evidence is authentic from here on; consume the nonce before anything else can fail.
        if (replayStore is null)
        {
            throw new ConsentDecisionException(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized, "External evidence cannot be verified.", 403);
        }

        var replayKey = $"external-evidence:{system!.Id:D}:{envelope.Nonce}";
        if (!await replayStore.SetIfNotExistsAsync(replayKey, new ReplayMarker(request.Id), ReplayRetention, ct).ConfigureAwait(false))
        {
            throw new ConsentDecisionException(ReplayCode, "External evidence was already used.", 409);
        }

        var approver = approverResolver is null
            ? null
            : await approverResolver.ResolveAsync(request.TenantId, envelope.Approver!, ct).ConfigureAwait(false);
        if (approver is null || !approver.IsAuthenticated)
        {
            // Unmapped identity: nothing is decided and the request stays pending.
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.ApproverUnresolved,
                "The external approver could not be mapped to a principal of the identity realm.",
                403);
        }

        return new ValidatedExternalDecision(
            approver,
            new Autheris.Application.Governance.Requests.ExternalDecisionEvidence(system.Id, envelope.ExternalReference, envelope.Reason));
    }

    private static void VerifySignature(string secret, ExternalDecisionEnvelope envelope)
    {
        var expected = Convert.FromHexString(ComputeSignature(secret, envelope));
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(envelope.Signature.Trim());
        }
        catch (FormatException)
        {
            throw Invalid("External evidence signature is invalid.");
        }

        if (!CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            throw Invalid("External evidence signature is invalid.");
        }
    }

    private static void ValidateSystemBinding(
        ExternalDecisionEnvelope envelope,
        ConsentRequest request,
        ConsentWorkflowRoute? route,
        ExternalWorkflowSystem? system)
    {
        if (system is null || !system.Enabled || system.Id != envelope.ExternalSystemId ||
            !string.Equals(system.TenantId, request.TenantId.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw NotAuthorized("The external workflow system is missing, disabled or belongs to another tenant.");
        }

        if (request.ExternalSystemId.HasValue && request.ExternalSystemId.Value != envelope.ExternalSystemId)
        {
            throw NotAuthorized("The external workflow system does not match the request.");
        }

        if (route?.ExternalSystemId is { } routeSystem && routeSystem != envelope.ExternalSystemId)
        {
            throw NotAuthorized("The external workflow system does not match the active route.");
        }

        if (!string.IsNullOrWhiteSpace(request.ExternalReference) &&
            !string.Equals(request.ExternalReference, envelope.ExternalReference, StringComparison.Ordinal))
        {
            throw NotAuthorized("The external reference does not match the request.");
        }

        if (envelope.Caller is not null && !string.IsNullOrWhiteSpace(system.ClientId))
        {
            var callerClientId = envelope.Caller.FindFirst("azp")?.Value
                ?? envelope.Caller.FindFirst("appid")?.Value
                ?? envelope.Caller.FindFirst("client_id")?.Value;
            if (!string.IsNullOrWhiteSpace(callerClientId) &&
                !string.Equals(callerClientId, system.ClientId, StringComparison.OrdinalIgnoreCase))
            {
                throw NotAuthorized("The caller does not match the registered external workflow system.");
            }
        }
    }

    private string? ResolveSecret(ExternalWorkflowSystem system)
    {
        var stored = system.WebhookSecretProtected;
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        if (stored.StartsWith("enc:v1:", StringComparison.Ordinal))
        {
            if (secretProtector is null)
            {
                return null;
            }

            try
            {
                return secretProtector.Unprotect(system.TenantId, system.Id.ToString("D"), stored);
            }
            catch (WebhookSecretUnavailableException)
            {
                return null;
            }
        }

        return secretProtector is null ? stored : null;
    }

    private static ConsentDecisionException Invalid(string message) => new(InvalidEvidenceCode, message, 403);

    private static ConsentDecisionException NotAuthorized(string message) =>
        new(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized, message, 403);

    private sealed record ReplayMarker(Guid RequestId);
}
