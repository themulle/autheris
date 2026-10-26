namespace Autheris.Application.Identity.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>Thrown when SCIM user deprovisioning is throttled. Carries only the retry delay; never user data.</summary>
public sealed class ScimMassChangeBlockedException(int retryAfterSeconds)
    : Exception("SCIM mass-change protection: deprovisioning is temporarily throttled.")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// 06-FU-02 (WAVE3-SCIM-SC-01..06): minimal fail-closed guard against SCIM mass deprovisioning. Counts distinct users locked by
/// SCIM (sources <see cref="ScimSources"/>) per tenant and realm in the window, using the shared lock table (cluster-wide), and blocks
/// further SCIM deactivations or deletes above the limit. Admin and manual locks are neither counted nor blocked. A count failure
/// propagates, so the SCIM operation fails closed. Concurrent nodes may overshoot by at most the in-flight requests (no distributed lock).
/// </summary>
public sealed class ScimMassChangeGuard
{
    public const string BlockedEventType = "SCIM_MASS_CHANGE_BLOCKED";

    /// <summary>The lock sources written by <see cref="ScimService"/>; admin locks use the initiator SID as source.</summary>
    public static readonly IReadOnlyList<string> ScimSources = ["SCIM_PUT", "SCIM_PATCH", "SCIM_DELETE"];

    private readonly IIdentityStoreRepository _repository;
    private readonly IOptionsMonitor<IdentityRealmsOptions> _options;
    private readonly IAuditWriter _auditWriter;
    private readonly TimeProvider _time;
    private readonly ILogger<ScimMassChangeGuard> _logger;
    private readonly ConcurrentDictionary<(string Tenant, string Realm), DateTimeOffset> _lastReported = new();

    public ScimMassChangeGuard(
        IIdentityStoreRepository repository,
        IOptionsMonitor<IdentityRealmsOptions> options,
        IAuditWriter auditWriter,
        TimeProvider? time = null,
        ILogger<ScimMassChangeGuard>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ScimMassChangeGuard>.Instance;
    }

    /// <summary>Call before <c>DeprovisioningService.DeprovisionAndLockSubjectAsync</c> for a SCIM-originated deactivation or delete.</summary>
    public async Task EnsureAllowedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
    {
        var settings = _options.CurrentValue.Scim?.MassChangeProtection ?? new ScimMassChangeProtectionOptions();
        if (!settings.Enabled)
        {
            return;
        }

        // An already-locked user is an idempotent repeat: the lock row is upserted, so it is neither counted again nor blocked.
        if (await _repository.IsSubjectLockedAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false))
        {
            return;
        }

        var now = _time.GetUtcNow();
        var window = TimeSpan.FromMinutes(settings.WindowMinutes);
        var stats = await _repository.CountLocksSinceAsync(
            tenantId, realmId, ScimSources, now - window, settings.MaxDestructiveOperations, ct).ConfigureAwait(false);
        if (stats.Count < settings.MaxDestructiveOperations)
        {
            return;
        }

        var retryAfter = window.TotalSeconds;
        if (stats.Oldest is { } oldest)
        {
            retryAfter = Math.Ceiling((oldest + window - now).TotalSeconds);
        }

        var retrySeconds = (int)Math.Clamp(retryAfter, 1, window.TotalSeconds);
        await ReportAsync(tenantId, realmId, stats.Count, settings, now, window, ct).ConfigureAwait(false);
        throw new ScimMassChangeBlockedException(retrySeconds);
    }

    private async Task ReportAsync(
        TenantId tenantId, string realmId, int count, ScimMassChangeProtectionOptions settings, DateTimeOffset now, TimeSpan window, CancellationToken ct)
    {
        // At most one audit event and one log line per realm per window, so a flood of blocked requests cannot flood either.
        var key = (tenantId.Value, realmId);
        DateTimeOffset? previous = null;
        while (true)
        {
            if (_lastReported.TryGetValue(key, out var last))
            {
                if (now - last < window)
                {
                    return;
                }

                if (_lastReported.TryUpdate(key, now, last))
                {
                    previous = last;
                    break;
                }
            }
            else if (_lastReported.TryAdd(key, now))
            {
                break;
            }
        }

        _logger.LogWarning(
            "SCIM mass-change protection blocked deprovisioning in realm {RealmId}: {Count} users locked within {WindowMinutes} minutes (limit {Limit}).",
            realmId, count, settings.WindowMinutes, settings.MaxDestructiveOperations);

        var entry = new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = BlockedEventType,
            ActorSid = new Sid("SCIM"),
            TargetTable = "IDENTITY_LOCKS",
            Decision = "DENY",
            DetailsJson = JsonSerializer.Serialize(new
            {
                tenant_id = tenantId.Value,
                realm_id = realmId,
                count,
                limit = settings.MaxDestructiveOperations,
                window_minutes = settings.WindowMinutes
            })
        };
        try
        {
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
        catch
        {
            // The window's first audit must not be lost: release the claim so the next block retries the audit.
            if (previous is { } prior)
            {
                _lastReported.TryUpdate(key, prior, now);
            }
            else
            {
                _lastReported.TryRemove(new KeyValuePair<(string Tenant, string Realm), DateTimeOffset>(key, now));
            }

            throw;
        }
    }
}
