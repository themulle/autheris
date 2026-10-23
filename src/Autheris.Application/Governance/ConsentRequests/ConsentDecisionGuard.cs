namespace Autheris.Application.Governance.ConsentRequests;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;

/// <summary>Outcome of <see cref="IConsentDecisionGuard.Evaluate"/>; <see cref="ReasonClass"/> is a fixed vocabulary, never free text.</summary>
public readonly record struct ConsentDecisionVerdict(bool Allowed, string ReasonClass)
{
    public static ConsentDecisionVerdict Allow() => new(true, string.Empty);

    public static ConsentDecisionVerdict Deny(string reasonClass) => new(false, reasonClass);
}

/// <summary>Fixed reason classes of a denied decision (audit and metric vocabulary).</summary>
public static class ConsentDecisionDenialReasons
{
    public const string Unauthenticated = "unauthenticated";
    public const string ReadOnly = "read-only";
    public const string NotPerson = "not-person";
}

/// <summary>
/// Decides whether a principal may decide (approve or reject) consent requests at all. Pure: no repository, no audit.
/// Resolved with <c>GetRequiredService</c> at the call site; a missing registration must fail the call, never allow it.
/// </summary>
public interface IConsentDecisionGuard
{
    ConsentDecisionVerdict Evaluate(ClaimsPrincipal? principal);
}

/// <summary>
/// SEC-113-R2-01: the one shared predicate "is this principal a person that may decide?". A person decides; an application never does.
/// Denied: unauthenticated or null principals, read-only tokens, <c>idtyp=app</c>, certificate (mTLS) subjects, and principals the
/// resolver classifies as service principal unless they carry a delegated scope claim (<c>scp</c> or its mapped URI; app-only tokens
/// never carry one) together with an immutable person identifier (oid or SID).
/// </summary>
public static class DecisionPerson
{
    private static bool IsClientCredentialsToken(ClaimsPrincipal principal)
    {
        var grantType = principal.FindFirst("gty")?.Value;
        if (string.Equals(grantType, "client-credentials", StringComparison.OrdinalIgnoreCase)
            || string.Equals(grantType, "client_credentials", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return principal.FindFirst("preferred_username")?.Value?.StartsWith("service-account-", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static readonly string[] PersonIdentifierClaimTypes =
    [
        ClaimTypes.PrimarySid,
        "objectSid",
        "onprem_sid",
        "primarysid",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid",
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier"
    ];

    public static ConsentDecisionVerdict Evaluate(ClaimsPrincipal? principal, IIdentitySubjectResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return ConsentDecisionVerdict.Deny(ConsentDecisionDenialReasons.Unauthenticated);
        }

        if (principal.IsReadOnly())
        {
            return ConsentDecisionVerdict.Deny(ConsentDecisionDenialReasons.ReadOnly);
        }

        if (string.Equals(principal.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase))
        {
            return ConsentDecisionVerdict.Deny(ConsentDecisionDenialReasons.NotPerson);
        }

        // GOV-15: OIDC servers (Keycloak) put a human-looking preferred_username ("service-account-<client>") into client-credentials tokens
        // without idtyp; such a token is an application, never a person.
        if (IsClientCredentialsToken(principal))
        {
            return ConsentDecisionVerdict.Deny(ConsentDecisionDenialReasons.NotPerson);
        }

        var subject = resolver.ResolveSubject(principal);
        if (subject.Provider == IdentityProviderType.ClientCertificate)
        {
            return ConsentDecisionVerdict.Deny(ConsentDecisionDenialReasons.NotPerson);
        }

        if (subject.Type == SubjectType.User)
        {
            return ConsentDecisionVerdict.Allow();
        }

        // The resolver says "application" (an app id without a human-name claim): a delegated token is still a person when it
        // carries a delegated scope and an immutable person identifier; an app-only token carries no scope.
        var delegated = TokenAccessScope.GetScopes(principal).Count > 0
            && PersonIdentifierClaimTypes.Any(t => !string.IsNullOrWhiteSpace(principal.FindFirst(t)?.Value));
        return delegated ? ConsentDecisionVerdict.Allow() : ConsentDecisionVerdict.Deny(ConsentDecisionDenialReasons.NotPerson);
    }
}

public sealed class ConsentDecisionGuard(IIdentitySubjectResolver resolver) : IConsentDecisionGuard
{
    public ConsentDecisionVerdict Evaluate(ClaimsPrincipal? principal) => DecisionPerson.Evaluate(principal, resolver);
}

/// <summary>Records denials of the guard; never throws (a denial is returned even when the audit store is down).</summary>
public interface IConsentDecisionDenialRecorder
{
    Task RecordAsync(ClaimsPrincipal? principal, string reasonClass, TenantId tenantId, CancellationToken ct = default);
}

/// <summary>
/// SEC-113-R2-02 (c): the first denial of a principal in a minute writes one Tier-A <c>CONSENT_DENIED</c> entry (reason class only);
/// further denials of the same principal in that minute only count in the metric, so a looping caller cannot flood the audit chain.
/// An audit failure is logged and counted and never turns the denial into an error or an allow.
/// </summary>
public sealed class ConsentDecisionDenialRecorder : IConsentDecisionDenialRecorder
{
    private const int DefaultMaxTrackedPrincipals = 10_000;

    private static readonly Counter<long> DeniedCounter = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_consent_decision_denied_total",
        description: "Consent request decisions refused by the decision guard, by reason class and whether a Tier-A entry was written");

    private readonly IAuditWriter _audit;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly int _maxTrackedPrincipals;
    private readonly ConcurrentDictionary<string, long> _lastAuditedMinute = new(StringComparer.OrdinalIgnoreCase);
    private long _lastPurgeMinute = long.MinValue;
    private int _purgeScanCount;

    public ConsentDecisionDenialRecorder(IAuditWriter audit, TimeProvider? time = null, ILogger<ConsentDecisionDenialRecorder>? logger = null)
        : this(audit, time, logger, DefaultMaxTrackedPrincipals)
    {
    }

    internal ConsentDecisionDenialRecorder(IAuditWriter audit, TimeProvider? time, ILogger? logger, int maxTrackedPrincipals)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedPrincipals, 1);
        _audit = audit;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _maxTrackedPrincipals = maxTrackedPrincipals;
    }

    /// <summary>Number of stale-entry purge scans performed (a purge runs at most once per minute window).</summary>
    internal int PurgeScanCount => Volatile.Read(ref _purgeScanCount);

    public async Task RecordAsync(ClaimsPrincipal? principal, string reasonClass, TenantId tenantId, CancellationToken ct = default)
    {
        try
        {
            var actor = principal?.GetUserSid()?.Value;
            var principalKey = string.IsNullOrWhiteSpace(actor) ? "anonymous" : actor;
            var key = string.Concat(tenantId.Value, "|", principalKey); // per tenant: one tenant's denials never mask another tenant's
            var minute = _time.GetUtcNow().ToUnixTimeSeconds() / 60;

            if (!ShouldAudit(key, minute))
            {
                DeniedCounter.Add(1, new KeyValuePair<string, object?>("reason", reasonClass), new KeyValuePair<string, object?>("audited", false));
                return;
            }

            DeniedCounter.Add(1, new KeyValuePair<string, object?>("reason", reasonClass), new KeyValuePair<string, object?>("audited", true));
            await _audit.RecordRequiredAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = AuditEventTypes.ConsentDenied,
                ActorSid = new Sid(principalKey),
                TargetTable = "CONSENT_REQUEST",
                Decision = "DENY",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new { reasonClass })
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled request is not an audit failure: nothing is counted or logged and the denial is still returned.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            GatewayDiagnostics.AuditWriteFailedCounter.Add(1, new KeyValuePair<string, object?>("tier", "required"));
            _logger?.LogError("A consent decision denial could not be audited ({ExceptionType}); the denial stands.", ex.GetType().Name);
        }
    }

    /// <summary>Entries only become stale when the minute changes, so one scan per minute window is enough; a flood at the cap costs O(1) per denial.</summary>
    private void PurgeStaleEntries(long minute)
    {
        var lastPurge = Interlocked.Read(ref _lastPurgeMinute);
        if (lastPurge >= minute || Interlocked.CompareExchange(ref _lastPurgeMinute, minute, lastPurge) != lastPurge)
        {
            return;
        }

        Interlocked.Increment(ref _purgeScanCount);
        foreach (var entry in _lastAuditedMinute)
        {
            if (entry.Value < minute)
            {
                _lastAuditedMinute.TryRemove(entry);
            }
        }
    }

    private bool ShouldAudit(string key, long minute)
    {
        if (_lastAuditedMinute.Count >= _maxTrackedPrincipals)
        {
            PurgeStaleEntries(minute);

            if (_lastAuditedMinute.Count >= _maxTrackedPrincipals)
            {
                return false; // bounded memory: under a flood of distinct principals only the metric counts
            }
        }

        while (true)
        {
            if (_lastAuditedMinute.TryGetValue(key, out var last))
            {
                if (last >= minute)
                {
                    return false;
                }

                if (_lastAuditedMinute.TryUpdate(key, minute, last))
                {
                    return true;
                }
            }
            else if (_lastAuditedMinute.TryAdd(key, minute))
            {
                return true;
            }
        }
    }
}

/// <summary>
/// A decision lost the race for a request: either the guarded status transition (<c>UPDATE ... WHERE id = @id AND status = @expected</c>)
/// matched no row, or the status read under the lock is no longer approvable for this step. Either way the request is no longer in the status
/// the decision was based on. Derives from <see cref="InvalidOperationException"/> so existing handlers of status conflicts keep working.
/// </summary>
public sealed class ConsentRequestStatusConflictException(Guid requestId)
    : InvalidOperationException($"Request {requestId} is no longer in the status the decision was based on.")
{
    public const string ErrorCode = "ACCESS_REQUEST_NOT_PENDING";

    public Guid RequestId { get; } = requestId;
}
