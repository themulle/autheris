using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Notifications;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Governance.Policy;

/// <summary>Resolves effective tenant access policy clamped by deployment ceilings and records audited updates.</summary>
public sealed class AccessPolicySettingsProvider(
    IAccessPolicySettingsRepository repository,
    IOptions<AccessPolicyOptions> deploymentOptions,
    IAuditWriter auditWriter,
    TimeProvider? timeProvider = null,
    INotificationWebhookUrlProtector? urlProtector = null) : IAccessPolicySettingsProvider
{
    private readonly IAccessPolicySettingsRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly AccessPolicyOptions _deployment = (deploymentOptions ?? throw new ArgumentNullException(nameof(deploymentOptions))).Value
        ?? throw new ArgumentNullException(nameof(deploymentOptions));
    private readonly IAuditWriter _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly INotificationWebhookUrlProtector? _urlProtector = urlProtector;

    public async Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default)
    {
        var stored = await _repository.GetAsync(tenant, ct).ConfigureAwait(false);
        return ClampToDeployment(tenant, stored?.Settings, stored?.Version ?? 0, stored?.UpdatedBy, stored?.UpdatedAt);
    }

    public async Task<AccessPolicyUpdateResult> UpdateAsync(
        TenantId tenant,
        TenantAccessPolicyUpdate update,
        int expectedVersion,
        Sid actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);

        if (!TryValidateUpdate(update, out var errorCode, out var errorMessage, out var normalizedUpdate))
        {
            return new AccessPolicyUpdateResult(AccessPolicyUpdateStatus.Invalid, ErrorCode: errorCode, ErrorMessage: errorMessage);
        }

        var beforeStored = await _repository.GetAsync(tenant, ct).ConfigureAwait(false);
        var beforeEffective = ClampToDeployment(
            tenant,
            beforeStored?.Settings,
            beforeStored?.Version ?? 0,
            beforeStored?.UpdatedBy,
            beforeStored?.UpdatedAt);

        normalizedUpdate = PrepareProtectedWebhookUpdate(normalizedUpdate, beforeStored?.Settings);

        var writeResult = await _repository.UpsertAsync(tenant, normalizedUpdate, expectedVersion, actor, ct).ConfigureAwait(false);
        if (!writeResult.Succeeded || writeResult.Stored is null)
        {
            return new AccessPolicyUpdateResult(
                AccessPolicyUpdateStatus.Conflict,
                ErrorCode: "VERSION_MISMATCH",
                ErrorMessage: "Tenant access policy version does not match If-Match.");
        }

        var afterEffective = ClampToDeployment(
            tenant,
            writeResult.Stored.Settings,
            writeResult.Stored.Version,
            writeResult.Stored.UpdatedBy,
            writeResult.Stored.UpdatedAt);

        try
        {
            await WritePolicyAuditAsync(tenant, actor, beforeEffective, afterEffective, ct).ConfigureAwait(false);
            if (beforeEffective.NotificationWebhookConfigured != afterEffective.NotificationWebhookConfigured
                || !string.Equals(beforeEffective.NotificationWebhookHost, afterEffective.NotificationWebhookHost, StringComparison.OrdinalIgnoreCase))
            {
                await WriteNotificationChannelChangedAuditAsync(tenant, actor, afterEffective, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            if (beforeStored is not null)
            {
                try
                {
                    await _repository.UpsertAsync(tenant, beforeStored.Settings, writeResult.Stored.Version, actor, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Compensation failure must still fail closed and propagate the audit exception.
                }
            }
            else
            {
                try
                {
                    await _repository.DeleteIfVersionAsync(tenant, writeResult.Stored.Version, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Compensation failure must still fail closed and propagate the audit exception.
                }
            }

            throw;
        }

        return new AccessPolicyUpdateResult(AccessPolicyUpdateStatus.Updated, afterEffective);
    }

    private TenantAccessPolicy ClampToDeployment(
        TenantId tenant,
        TenantAccessPolicyUpdate? stored,
        int version,
        string? updatedBy,
        DateTimeOffset? updatedAt)
    {
        var defaults = TenantAccessPolicy.FromDeploymentDefaults(tenant, _deployment);
        if (stored is null)
        {
            return defaults with { Version = version, UpdatedBy = updatedBy, UpdatedAt = updatedAt };
        }

        var maxDataOwnerDays = ClampDay(stored.MaxDataOwnerDays, defaults.MaxDataOwnerDays);
        var maxGovernanceDays = ClampDay(stored.MaxGovernanceDays, defaults.MaxGovernanceDays);
        var maxItAdminDays = ClampDay(stored.MaxItAdminDays, defaults.MaxItAdminDays);
        var maxSelfGrantDays = ClampDay(stored.MaxSelfGrantDays, defaults.MaxSelfGrantDays);
        var selfGrantCeiling = ClampSelfGrantCeiling(stored.SelfGrantCeiling, defaults.SelfGrantCeiling);
        var reminderDays = ClampReminderDays(stored.ReminderDays, defaults.ReminderDays);
        var visibility = ClampVisibility(stored.DefaultContainerVisibility, defaults.DefaultContainerVisibility);
        var internalPolicy = ClampInternal(stored.Internal, defaults.Internal);

        return new TenantAccessPolicy(
            Tenant: tenant,
            MaxDataOwnerDays: maxDataOwnerDays,
            MaxGovernanceDays: maxGovernanceDays,
            MaxItAdminDays: maxItAdminDays,
            MaxSelfGrantDays: maxSelfGrantDays,
            SelfGrantCeiling: selfGrantCeiling,
            ReminderDays: reminderDays,
            DefaultContainerVisibility: visibility,
            Internal: internalPolicy,
            ServeImportSamples: defaults.ServeImportSamples && (stored.ServeImportSamples ?? defaults.ServeImportSamples),
            QuarantineSuspectedPiiSamples: defaults.QuarantineSuspectedPiiSamples || (stored.QuarantineSuspectedPiiSamples ?? defaults.QuarantineSuspectedPiiSamples),
            MaxBundleItems: ClampDay(stored.MaxBundleItems, defaults.MaxBundleItems),
            MaxPendingRequestsPerRequester: ClampDay(stored.MaxPendingRequestsPerRequester, defaults.MaxPendingRequestsPerRequester),
            MaxTablesPerAssignment: ClampDay(stored.MaxTablesPerAssignment, defaults.MaxTablesPerAssignment),
            MaxPageSize: ClampDay(stored.MaxPageSize, defaults.MaxPageSize),
            Version: version,
            UpdatedBy: updatedBy,
            UpdatedAt: updatedAt,
            NotificationWebhookConfigured: !string.IsNullOrWhiteSpace(stored.NotificationWebhookUrlProtected),
            NotificationWebhookHost: string.IsNullOrWhiteSpace(stored.NotificationWebhookUrlProtected) ? null : stored.NotificationWebhookHost,
            NotificationWebhookUrlProtected: stored.NotificationWebhookUrlProtected);
    }

    private static int ClampDay(int? candidate, int ceiling) =>
        candidate is > 0 ? Math.Min(candidate.Value, ceiling) : ceiling;

    private static ProtectionClass ClampSelfGrantCeiling(ProtectionClass? candidate, ProtectionClass deploymentCeiling)
    {
        var safeDeployment = deploymentCeiling is ProtectionClass.Public or ProtectionClass.Internal or ProtectionClass.Confidential
            ? deploymentCeiling
            : ProtectionClass.Confidential;

        if (candidate is null)
        {
            return safeDeployment;
        }

        var safeCandidate = candidate.Value is ProtectionClass.Public or ProtectionClass.Internal or ProtectionClass.Confidential
            ? candidate.Value
            : ProtectionClass.Public;

        return safeCandidate <= safeDeployment ? safeCandidate : safeDeployment;
    }

    private static IReadOnlyList<int> ClampReminderDays(IReadOnlyList<int>? candidate, IReadOnlyList<int> deploymentDefault)
    {
        if (candidate is null)
        {
            return deploymentDefault;
        }

        return candidate
            .Where(d => d is >= 1 and <= AccessPolicyOptionsValidator.MaxReminderDay)
            .Distinct()
            .OrderByDescending(d => d)
            .Take(AccessPolicyOptionsValidator.MaxReminderEntries)
            .ToArray();
    }

    private static CatalogVisibilityMode ClampVisibility(CatalogVisibilityMode? candidate, CatalogVisibilityMode deploymentDefault)
    {
        if (deploymentDefault == CatalogVisibilityMode.OnRequest || candidate is null)
        {
            return deploymentDefault;
        }

        return candidate.Value.NormalizeOrOnRequest();
    }

    private static InternalAudiencePolicy ClampInternal(InternalAudiencePolicyUpdate? candidate, InternalAudiencePolicy deploymentDefault)
    {
        if (candidate is null)
        {
            return deploymentDefault;
        }

        var issuers = ClampAllowlist(
            NormalizeGuids(candidate.TrustedIssuerTenantIds),
            deploymentDefault.TrustedIssuerTenantIds,
            StringComparer.OrdinalIgnoreCase);

        var groups = ClampAllowlist(
            NormalizeSids(candidate.EmployeeGroupSids),
            deploymentDefault.EmployeeGroupSids,
            StringComparer.Ordinal);

        var spClients = ClampAllowlist(
            NormalizeClientIds(candidate.TrustedServicePrincipalClientIds),
            deploymentDefault.TrustedServicePrincipalClientIds,
            StringComparer.Ordinal);

        var requireMember = deploymentDefault.RequireMemberIndicator || (candidate.RequireMemberIndicator ?? deploymentDefault.RequireMemberIndicator);
        return new InternalAudiencePolicy(issuers, groups, requireMember, spClients);
    }

    private static IReadOnlyList<string> ClampAllowlist(
        IReadOnlyList<string>? candidate,
        IReadOnlyList<string> deploymentAllowlist,
        StringComparer comparer)
    {
        if (candidate is null)
        {
            return deploymentAllowlist;
        }

        if (deploymentAllowlist.Count == 0)
        {
            return candidate;
        }

        var allowed = new HashSet<string>(deploymentAllowlist, comparer);
        return candidate.Where(allowed.Contains).Distinct(comparer).ToArray();
    }

    private static bool TryValidateUpdate(
        TenantAccessPolicyUpdate update,
        out string? errorCode,
        out string? errorMessage,
        out TenantAccessPolicyUpdate normalized)
    {
        normalized = update;
        if (update.SelfGrantCeiling is { } ceiling && (!Enum.IsDefined(ceiling) || ceiling == ProtectionClass.Secret))
        {
            errorCode = "SELF_GRANT_CEILING_INVALID";
            errorMessage = "SelfGrantCeiling cannot be Secret and must be Public, Internal, or Confidential.";
            return false;
        }

        if (!IsValidOptionalPositive(update.MaxDataOwnerDays, 36500)
            || !IsValidOptionalPositive(update.MaxGovernanceDays, 36500)
            || !IsValidOptionalPositive(update.MaxItAdminDays, 3650)
            || !IsValidOptionalPositive(update.MaxSelfGrantDays, 3650)
            || !IsValidOptionalPositive(update.MaxBundleItems, 1000)
            || !IsValidOptionalPositive(update.MaxPendingRequestsPerRequester, 10000)
            || !IsValidOptionalPositive(update.MaxTablesPerAssignment, 10000)
            || !IsValidOptionalPositive(update.MaxPageSize, 5000))
        {
            errorCode = "INVALID_ACCESS_POLICY";
            errorMessage = "Numeric policy limits must be positive integers within valid operational bounds.";
            return false;
        }

        if (update.DefaultContainerVisibility is { } vis && !Enum.IsDefined(vis))
        {
            errorCode = "INVALID_ACCESS_POLICY";
            errorMessage = "DefaultContainerVisibility must be Open or OnRequest.";
            return false;
        }

        if (update.ReminderDays is not null)
        {
            var reminderFailures = new List<string>();
            AccessPolicyOptionsValidator.ValidateReminderDays(update.ReminderDays, reminderFailures);
            if (reminderFailures.Count > 0)
            {
                errorCode = "INVALID_ACCESS_POLICY";
                errorMessage = reminderFailures[0];
                return false;
            }
        }

        var internalFailures = new List<string>();
        if (update.Internal is not null)
        {
            AccessPolicyOptionsValidator.ValidateIssuerList(update.Internal.TrustedIssuerTenantIds, internalFailures);
            AccessPolicyOptionsValidator.ValidateEmployeeGroupSids(update.Internal.EmployeeGroupSids, internalFailures);
            AccessPolicyOptionsValidator.ValidateServicePrincipalClients(update.Internal.TrustedServicePrincipalClientIds, internalFailures);
        }

        if (internalFailures.Count > 0)
        {
            errorCode = "INVALID_ACCESS_POLICY";
            errorMessage = internalFailures[0];
            return false;
        }

        if (update.NotificationWebhookUrlProtected is not null)
        {
            errorCode = "INVALID_ACCESS_POLICY";
            errorMessage = "Protected notification webhook URL cannot be supplied directly.";
            return false;
        }

        if (update.NotificationWebhookUrl is { Length: > 0 } &&
            !NotificationWebhookUrlUtility.TryValidateHttpsUrl(update.NotificationWebhookUrl, out _, out var urlError))
        {
            errorCode = "INVALID_ACCESS_POLICY";
            errorMessage = urlError ?? "Invalid notification webhook URL.";
            return false;
        }

        normalized = update with
        {
            ReminderDays = update.ReminderDays?.Distinct().OrderByDescending(d => d).ToArray(),
            Internal = update.Internal is null
                ? null
                : new InternalAudiencePolicyUpdate(
                    NormalizeGuids(update.Internal.TrustedIssuerTenantIds),
                    NormalizeSids(update.Internal.EmployeeGroupSids),
                    update.Internal.RequireMemberIndicator,
                    NormalizeClientIds(update.Internal.TrustedServicePrincipalClientIds)),
            NotificationWebhookHost = null
        };

        errorCode = null;
        errorMessage = null;
        return true;
    }

    private static bool IsValidOptionalPositive(int? value, int upperBound) =>
        value is null || (value.Value >= 1 && value.Value <= upperBound);

    private static IReadOnlyList<string>? NormalizeGuids(IReadOnlyList<string>? raw) =>
        raw?.Select(x => Guid.Parse(x.Trim()).ToString("D", CultureInfo.InvariantCulture))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string>? NormalizeSids(IReadOnlyList<string>? raw) =>
        raw?.Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string>? NormalizeClientIds(IReadOnlyList<string>? raw) =>
        raw?.Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private Task WritePolicyAuditAsync(
        TenantId tenant,
        Sid actor,
        TenantAccessPolicy before,
        TenantAccessPolicy after,
        CancellationToken ct)
    {
        var payload = new
        {
            before = ProjectForAudit(before),
            after = ProjectForAudit(after),
            previousVersion = before.Version,
            version = after.Version
        };

        return _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = tenant,
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = AuditEventTypes.PolicyChanged,
            ActorSid = actor,
            TargetTable = "access_policy_settings",
            Decision = "ALLOW",
            TraceId = Activity.Current?.TraceId.ToString() ?? string.Empty,
            DetailsJson = JsonSerializer.Serialize(payload)
        }, ct);
    }

    private static object ProjectForAudit(TenantAccessPolicy policy) => new
    {
        maxDataOwnerDays = policy.MaxDataOwnerDays,
        maxGovernanceDays = policy.MaxGovernanceDays,
        maxItAdminDays = policy.MaxItAdminDays,
        maxSelfGrantDays = policy.MaxSelfGrantDays,
        selfGrantCeiling = policy.SelfGrantCeiling.ToCanonicalName(),
        reminderDays = policy.ReminderDays,
        defaultContainerVisibility = policy.DefaultContainerVisibility.ToCanonicalName(),
        @internal = new
        {
            trustedIssuerTenantIds = policy.Internal.TrustedIssuerTenantIds,
            employeeGroupSids = policy.Internal.EmployeeGroupSids,
            requireMemberIndicator = policy.Internal.RequireMemberIndicator,
            trustedServicePrincipalClientIds = policy.Internal.TrustedServicePrincipalClientIds
        },
        serveImportSamples = policy.ServeImportSamples,
        quarantineSuspectedPiiSamples = policy.QuarantineSuspectedPiiSamples,
        maxBundleItems = policy.MaxBundleItems,
        maxPendingRequestsPerRequester = policy.MaxPendingRequestsPerRequester,
        maxTablesPerAssignment = policy.MaxTablesPerAssignment,
        maxPageSize = policy.MaxPageSize
    };

    private TenantAccessPolicyUpdate PrepareProtectedWebhookUpdate(
        TenantAccessPolicyUpdate normalized,
        TenantAccessPolicyUpdate? existing)
    {
        if (normalized.NotificationWebhookUrl is null)
        {
            return normalized with
            {
                NotificationWebhookUrl = null,
                NotificationWebhookUrlProtected = existing?.NotificationWebhookUrlProtected,
                NotificationWebhookHost = existing?.NotificationWebhookHost
            };
        }

        if (string.IsNullOrWhiteSpace(normalized.NotificationWebhookUrl))
        {
            return normalized with
            {
                NotificationWebhookUrl = null,
                NotificationWebhookUrlProtected = null,
                NotificationWebhookHost = null
            };
        }

        var protector = _urlProtector
            ?? throw new InvalidOperationException("Notification webhook URL protector is unavailable.");
        if (!NotificationWebhookUrlUtility.TryValidateHttpsUrl(normalized.NotificationWebhookUrl, out var uri, out var error) || uri is null)
        {
            throw new ArgumentException(error ?? "Invalid notification webhook URL.", nameof(normalized));
        }

        return normalized with
        {
            NotificationWebhookUrl = null,
            NotificationWebhookUrlProtected = protector.Protect(uri.AbsoluteUri),
            NotificationWebhookHost = NotificationWebhookUrlUtility.ExtractSafeHost(uri)
        };
    }

    private Task WriteNotificationChannelChangedAuditAsync(
        TenantId tenant,
        Sid actor,
        TenantAccessPolicy after,
        CancellationToken ct)
    {
        var detailsJson = JsonSerializer.Serialize(new
        {
            channel = "governance_notifications",
            configured = after.NotificationWebhookConfigured,
            hostHash = NotificationWebhookUrlUtility.ComputeHostHash(after.NotificationWebhookHost),
            source = "tenant_policy"
        });

        return _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = tenant,
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = AuditEventTypes.NotificationChannelChanged,
            ActorSid = actor,
            TargetTable = "access_policy_settings",
            Decision = after.NotificationWebhookConfigured ? "CONFIGURED" : "UNCONFIGURED",
            TraceId = Activity.Current?.TraceId.ToString() ?? string.Empty,
            DetailsJson = detailsJson
        }, ct);
    }
}
