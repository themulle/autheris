namespace Autheris.Application.Governance.Grants;

using System;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>
/// Capacity-based grant lifetime policy. Maxima derive solely from the decider's <see cref="ApproverCapacity"/>
/// (clamped below deployment ceilings from <see cref="AccessPolicyOptions"/>), never from object protection class
/// or service-principal status. Single decision everywhere; the only four-eyes review (grants on <c>secret</c> data) is
/// <see cref="Autheris.Application.Governance.Requests.SecretFourEyesRule"/> in the permission request engine (issue #1132).
/// </summary>
public sealed class GrantPolicy(
    IOptions<IndividualGrantOptions>? options = null,
    IGrantValidityPolicy? validityPolicy = null,
    IOptions<AccessPolicyOptions>? accessPolicyOptions = null) : IGrantPolicy
{
    private readonly IndividualGrantOptions _options = options?.Value ?? new IndividualGrantOptions();
    private readonly AccessPolicyOptions _accessPolicy = accessPolicyOptions?.Value ?? new AccessPolicyOptions();
    private readonly bool _hasExplicitAccessPolicy = accessPolicyOptions is not null;
    private readonly IGrantValidityPolicy? _explicitValidity = validityPolicy;
    private readonly IGrantValidityPolicy _validity = validityPolicy ?? GrantValidityPolicy.CreateDefault();

    public GrantDecision Evaluate(GrantRequest request)
    {
        try
        {
            return EvaluateCore(request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return GrantDecision.Deny(GrantErrorCodes.Indeterminate, "The grant could not be evaluated; denied (fail closed).");
        }
    }

    private GrantDecision EvaluateCore(GrantRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!Enum.IsDefined(r.GranteeType) || !Enum.IsDefined(r.Kind))
        {
            return GrantDecision.Deny(GrantErrorCodes.Indeterminate, "Unknown grantee type or grant kind.");
        }

        if (r.Effect == ConsentEffect.Deny)
        {
            return new GrantDecision(true, null, GrantClass.Standing, "Deny grants only restrict access and have no maximum.");
        }

        var nowUtc = r.Now.ToUniversalTime();
        var isCapacityEvaluated = r.Capacity.HasValue || r.RequestKind.HasValue || r.SelfGrant;

        if (!isCapacityEvaluated && r.GranteeType == GranteeType.Group)
        {
            var tenantMax = GrantTimeMath.AddSaturating(nowUtc, TenantMaximum(r.Kind));
            if (r.ValidTo is { } standingTo && standingTo.ToUniversalTime() > tenantMax)
            {
                return GrantDecision.Deny(
                    GrantErrorCodes.ExceedsMaximum,
                    $"The requested expiry exceeds the maximum of {FormatSpan(TenantMaximum(r.Kind))}.",
                    GrantClass.Standing,
                    tenantMax);
            }

            return new GrantDecision(true, null, GrantClass.Standing, "Standing group grant.");
        }

        var effectiveCapacity = r.SelfGrant
            ? ApproverCapacity.SelfGrant
            : r.Capacity ?? ApproverCapacity.DataOwner;

        var cls = MapGrantClass(effectiveCapacity);
        var disabled = CustomerDisabled(r, cls);
        if (disabled != null)
        {
            return GrantDecision.Deny(GrantErrorCodes.DisabledForRealm, disabled, cls);
        }

        if (effectiveCapacity == ApproverCapacity.SelfGrant || r.SelfGrant)
        {
            if (r.ProtectionClass == ProtectionClass.Secret)
            {
                return GrantDecision.Deny(
                    GrantErrorCodes.SelfGrantSecretRefused,
                    "Secret objects cannot be accessed via self-grant.",
                    GrantClass.SelfGrant);
            }

            var ceiling = ResolveSelfGrantCeiling(r.TenantPolicy);
            if (r.ProtectionClass > ceiling)
            {
                return GrantDecision.Deny(
                    GrantErrorCodes.SelfGrantExceedsCeiling,
                    $"Protection class '{r.ProtectionClass}' exceeds the self-grant ceiling '{ceiling}'.",
                    GrantClass.SelfGrant);
            }
        }

        var maxSpan = ResolveMaxSpan(r, effectiveCapacity);
        var maxValidTo = GrantTimeMath.AddSaturating(nowUtc, maxSpan);

        var suggest = false;
        if (r.Kind == GrantKind.Renewal)
        {
            if (string.IsNullOrWhiteSpace(r.Renewer) ||
                SeparationOfDutiesService.IdentifiersOverlap(r.Renewer, r.GranteeRef))
            {
                return GrantDecision.Deny(
                    GrantErrorCodes.RenewalByGrantee,
                    "A grant must be renewed by an eligible approver, never by the grantee.",
                    cls,
                    maxValidTo);
            }

            if (r.RenewalCount >= _options.CampaignRequiredAfterRenewals)
            {
                return GrantDecision.Deny(
                    GrantErrorCodes.RenewalRequiresCampaign,
                    "This grant was renewed repeatedly; a campaign decision is required instead of a direct extension.",
                    cls,
                    maxValidTo) with { RequiresCampaignDecision = true };
            }

            suggest = r.RenewalCount >= _options.RoleConversionHintAfterRenewals;
        }

        var isCatalogRequest = r.RequestKind == ConsentRequestKind.Catalog;
        if (r.ValidTo is null)
        {
            if (isCatalogRequest)
            {
                return new GrantDecision(true, null, cls, "Allowed (catalog visibility without end date).", null, suggest);
            }

            var missingCode = isCapacityEvaluated ? GrantErrorCodes.EndDateRequired : GrantErrorCodes.RequiresExpiry;
            return GrantDecision.Deny(missingCode, "An explicit expiry (validTo) in UTC is required.", cls, maxValidTo);
        }

        var validToUtc = r.ValidTo.Value.ToUniversalTime();
        if (validToUtc <= nowUtc)
        {
            return GrantDecision.Deny(GrantErrorCodes.ExpiryInPast, "The expiry of the grant must lie in the future.", cls, maxValidTo);
        }

        if (validToUtc > maxValidTo)
        {
            var limitCode = isCapacityEvaluated ? GrantErrorCodes.ExceedsApproverLimit : GrantErrorCodes.ExceedsMaximum;
            return GrantDecision.Deny(
                limitCode,
                $"The requested expiry exceeds the maximum of {FormatSpan(maxSpan)} for approver capacity {effectiveCapacity}.",
                cls,
                maxValidTo);
        }

        if (!isCapacityEvaluated &&
            (r.MaskingMode == MaskingPolicyMode.Unmasked || r.PrivilegedRight) &&
            string.IsNullOrWhiteSpace(r.Justification))
        {
            return GrantDecision.Deny(GrantErrorCodes.JustificationRequired, "A justification is required for this grant.", cls, maxValidTo);
        }

        // Issue #1132: no sensitivity-based four-eyes step. A second approval exists only for grants on secret data and only through
        // SecretFourEyesRule in the permission request engine; the declarative paths and the MCP access plan refuse secret targets themselves.
        return new GrantDecision(true, maxValidTo, cls, "Allowed.", null, suggest);
    }

    private TimeSpan ResolveMaxSpan(GrantRequest r, ApproverCapacity capacity)
    {
        var capacitySpan = ResolveCapacityLimit(capacity, r.TenantPolicy);
        var isCapacityEvaluated = r.Capacity.HasValue || r.RequestKind.HasValue || r.SelfGrant;

        TimeSpan? optionalLimit = null;
        if (r.Kind == GrantKind.ManifestPolicy)
        {
            optionalLimit = _validity.DeclarativeMaxDuration;
        }
        else if (_explicitValidity is not null)
        {
            optionalLimit = _explicitValidity.MaxDuration;
        }
        else if (!isCapacityEvaluated && !_hasExplicitAccessPolicy)
        {
            optionalLimit = _validity.MaxDuration;
        }

        int? legacyOptionDays = capacity switch
        {
            ApproverCapacity.DataOwner or ApproverCapacity.Governance => r.PrivilegedRight
                ? _options.PrivilegedMaxDays
                : r.MaskingMode == MaskingPolicyMode.Unmasked
                    ? _options.UnmaskedMaxDays
                    : (r.Sensitivity == GrantSensitivity.Sensitive ? _options.SensitiveMaxDays : _options.RegularMaxDays),
            _ => null
        };

        if (r.CustomerRealm && capacity is ApproverCapacity.DataOwner or ApproverCapacity.Governance && _options.CustomerRegularMaxDays.HasValue)
        {
            legacyOptionDays = GrantTimeMath.MinLimit(
                legacyOptionDays.HasValue ? GrantTimeMath.DaysToSpan(legacyOptionDays.Value) : null,
                GrantTimeMath.DaysToSpan(_options.CustomerRegularMaxDays.Value)) is { } m
                ? (int)Math.Ceiling(m.TotalDays)
                : legacyOptionDays;
        }

        var legacySpan = legacyOptionDays is { } d
            ? (d <= 0 ? TimeSpan.FromDays(1) : GrantTimeMath.DaysToSpan(d))
            : (TimeSpan?)null;

        var combined = GrantTimeMath.MinLimit(capacitySpan, optionalLimit);
        return GrantTimeMath.MinLimit(combined, legacySpan)!.Value;
    }

    private TimeSpan ResolveCapacityLimit(ApproverCapacity capacity, TenantAccessPolicy? tenantPolicy)
    {
        var deploymentDays = capacity switch
        {
            ApproverCapacity.DataOwner => Math.Clamp(_accessPolicy.MaxDataOwnerDays, 1, 36500),
            ApproverCapacity.Governance => Math.Clamp(_accessPolicy.MaxGovernanceDays, 1, 36500),
            ApproverCapacity.ItAdmin => Math.Clamp(_accessPolicy.MaxItAdminDays, 1, 3650),
            ApproverCapacity.SelfGrant => Math.Clamp(_accessPolicy.MaxSelfGrantDays, 1, 3650),
            _ => Math.Clamp(_accessPolicy.MaxItAdminDays, 1, 3650)
        };

        var effectiveDays = deploymentDays;
        if (tenantPolicy is not null)
        {
            var tenantDays = capacity switch
            {
                ApproverCapacity.DataOwner => tenantPolicy.MaxDataOwnerDays,
                ApproverCapacity.Governance => tenantPolicy.MaxGovernanceDays,
                ApproverCapacity.ItAdmin => tenantPolicy.MaxItAdminDays,
                ApproverCapacity.SelfGrant => tenantPolicy.MaxSelfGrantDays,
                _ => tenantPolicy.MaxItAdminDays
            };

            if (tenantDays > 0)
            {
                effectiveDays = Math.Min(tenantDays, deploymentDays);
            }
        }

        return GrantTimeMath.DaysToSpan(Math.Max(1, effectiveDays));
    }

    private ProtectionClass ResolveSelfGrantCeiling(TenantAccessPolicy? tenantPolicy)
    {
        var deploymentCeiling = _accessPolicy.SelfGrantCeiling is ProtectionClass.Public or ProtectionClass.Internal or ProtectionClass.Confidential
            ? _accessPolicy.SelfGrantCeiling
            : ProtectionClass.Confidential;

        if (tenantPolicy is null)
        {
            return deploymentCeiling;
        }

        var tenantCeiling = tenantPolicy.SelfGrantCeiling is ProtectionClass.Public or ProtectionClass.Internal or ProtectionClass.Confidential
            ? tenantPolicy.SelfGrantCeiling
            : ProtectionClass.Confidential;

        return tenantCeiling < deploymentCeiling ? tenantCeiling : deploymentCeiling;
    }

    private static GrantClass MapGrantClass(ApproverCapacity capacity) => capacity switch
    {
        ApproverCapacity.DataOwner => GrantClass.DataOwner,
        ApproverCapacity.Governance => GrantClass.Governance,
        ApproverCapacity.ItAdmin => GrantClass.ItAdmin,
        ApproverCapacity.SelfGrant => GrantClass.SelfGrant,
        _ => GrantClass.ItAdmin
    };

    private TimeSpan TenantMaximum(GrantKind kind) =>
        kind == GrantKind.ManifestPolicy ? _validity.DeclarativeMaxDuration : _validity.MaxDuration;

    private string? CustomerDisabled(GrantRequest r, GrantClass _)
    {
        if (!r.CustomerRealm)
        {
            return null;
        }

        if (r.MaskingMode == MaskingPolicyMode.Unmasked && !_options.AllowCustomerUnmasked)
        {
            return "Individual Unmasked access is disabled for customer realms.";
        }

        if (r.PrivilegedRight && !_options.AllowCustomerPrivileged)
        {
            return "Individual privileged rights are disabled for customer realms.";
        }

        return null;
    }

    private static string FormatSpan(TimeSpan s) => s.TotalHours < 24 * 2 ? $"{s.TotalHours:0} hours" : $"{s.TotalDays:0} days";
}
