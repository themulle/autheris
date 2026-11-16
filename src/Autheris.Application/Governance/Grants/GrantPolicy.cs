namespace Autheris.Application.Governance.Grants;

using System;
using System.Linq;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// 16 time-bound individual grant policy. Individual grants (<see cref="GranteeType.User"/>, <see cref="GranteeType.ServicePrincipal"/>)
/// always need an explicit <c>ValidTo</c> within their effective maximum: the minimum of the operator-configured class limit
/// (<see cref="IndividualGrantOptions"/>, optional) and the required tenant maximum of <see cref="IGrantValidityPolicy"/>. There
/// are no compiled-in ceilings. Group and role grants are standing and take no class limit (the validity policy bounds them).
/// Renewal is a new approval, never by the grantee. Any unexpected state denies.
/// </summary>
public sealed class GrantPolicy(IOptions<IndividualGrantOptions>? options = null, IGrantValidityPolicy? validityPolicy = null) : IGrantPolicy
{
    private readonly IndividualGrantOptions _options = options?.Value ?? new IndividualGrantOptions();

    // Unit-level construction without DI falls back to the test default; production composition always supplies the
    // configured (required) validity options.
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
            return new GrantDecision(true, null, false, GrantRecertClass.None, GrantClass.Standing, "Deny grants only restrict access and have no maximum.");
        }

        if (r.GranteeType is GranteeType.Group or GranteeType.Role)
        {
            // Standing grants take no class limit, but an explicit expiry is bounded by the tenant maximum like everyone else.
            var tenantMax = GrantTimeMath.AddSaturating(r.Now, TenantMaximum(r.Kind));
            if (r.ValidTo is { } standingTo && standingTo > tenantMax)
            {
                return GrantDecision.Deny(GrantErrorCodes.ExceedsMaximum,
                    $"The requested expiry exceeds the maximum of {FormatSpan(TenantMaximum(r.Kind))}.", GrantClass.Standing, tenantMax);
            }

            return new GrantDecision(true, null, false, GrantRecertClass.None, GrantClass.Standing, "Standing group/role grant.");
        }

        var cls = Classify(r);
        var (maxSpan, recert, fourEyes) = Limits(cls, r.Kind);
        var disabled = CustomerDisabled(r, cls);
        if (disabled != null)
        {
            return GrantDecision.Deny(GrantErrorCodes.DisabledForRealm, disabled, cls);
        }

        var maxValidTo = MaxValidTo(r.Now, maxSpan);

        // Rule 3: renewal is a new approval by someone else, capped per class and handed to a campaign from the Nth renewal.
        var suggest = false;
        if (r.Kind == GrantKind.Renewal)
        {
            if (string.IsNullOrWhiteSpace(r.Renewer) ||
                string.Equals(r.Renewer.Trim(), r.GranteeRef?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return GrantDecision.Deny(GrantErrorCodes.RenewalByGrantee, "A grant must be renewed by an owner or delegate, never by the grantee.", cls, maxValidTo);
            }

            if (r.RenewalCount >= _options.CampaignRequiredAfterRenewals)
            {
                return GrantDecision.Deny(GrantErrorCodes.RenewalRequiresCampaign,
                    "This grant was renewed repeatedly; a campaign decision is required instead of a direct extension.", cls, maxValidTo) with { RequiresCampaignDecision = true };
            }

            suggest = r.RenewalCount >= _options.RoleConversionHintAfterRenewals;
        }

        // Rule 1: mandatory explicit expiry for every individual grant.
        if (r.ValidTo is null)
        {
            return GrantDecision.Deny(GrantErrorCodes.RequiresExpiry, "An individual grant requires an explicit expiry (validUntil).", cls, maxValidTo);
        }

        if (r.ValidTo.Value <= r.Now)
        {
            return GrantDecision.Deny(GrantErrorCodes.ExpiryInPast, "The expiry of the grant must lie in the future.", cls, maxValidTo);
        }

        // Rule 2: maximum duration = min(configured class limit, tenant maximum).
        if (r.ValidTo.Value > maxValidTo)
        {
            return GrantDecision.Deny(GrantErrorCodes.ExceedsMaximum,
                $"The requested expiry exceeds the maximum of {FormatSpan(maxSpan)} for this grant class.", cls, maxValidTo);
        }

        if ((cls is GrantClass.IndividualUnmasked or GrantClass.IndividualPrivileged) &&
            string.IsNullOrWhiteSpace(r.Justification))
        {
            return GrantDecision.Deny(GrantErrorCodes.JustificationRequired, "A justification is required for this grant class.", cls, maxValidTo);
        }

        // Four-eyes is only enforced once approvals are presented (activation time): owner + second approver for sensitive
        // classes, approver != requester != grantee.
        if (fourEyes && r.Approvers is not null)
        {
            var distinct = r.Approvers
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .Where(a => !string.Equals(a, r.Requester?.Trim(), StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(a, r.GranteeRef?.Trim(), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var needed = cls is GrantClass.ServicePrincipal ? 1 : 2;

            if (distinct < needed)
            {
                return GrantDecision.Deny(GrantErrorCodes.FourEyesRequired,
                    $"This grant class requires {needed} independent approver(s) different from requester and grantee.", cls, maxValidTo);
            }
        }

        return new GrantDecision(true, maxValidTo, fourEyes, recert, cls, "Allowed.", null, suggest);
    }

    private GrantClass Classify(GrantRequest r)
    {
        if (r.GranteeType == GranteeType.ServicePrincipal)
        {
            return GrantClass.ServicePrincipal;
        }

        if (r.PrivilegedRight)
        {
            return GrantClass.IndividualPrivileged;
        }

        if (r.MaskingMode == MaskingPolicyMode.Unmasked)
        {
            return GrantClass.IndividualUnmasked;
        }

        if (r.Sensitivity == GrantSensitivity.Sensitive)
        {
            return GrantClass.Sensitive;
        }

        return r.CustomerRealm ? GrantClass.CustomerRegular : GrantClass.Regular;
    }

    private (TimeSpan Max, GrantRecertClass Recert, bool FourEyes) Limits(GrantClass cls, GrantKind kind)
    {
        var (configured, recert, fourEyes) = cls switch
        {
            GrantClass.Regular => (_options.RegularMaxDays, GrantRecertClass.Campaign365, false),
            GrantClass.CustomerRegular => (_options.CustomerRegularMaxDays, GrantRecertClass.CustomerAdminAttestation, false),
            GrantClass.Sensitive => (_options.SensitiveMaxDays, GrantRecertClass.Campaign90, true),
            GrantClass.IndividualUnmasked => (_options.UnmaskedMaxDays, GrantRecertClass.Campaign90, true),
            GrantClass.IndividualPrivileged => (_options.PrivilegedMaxDays, GrantRecertClass.Campaign90, true),
            GrantClass.ServicePrincipal => (_options.ServicePrincipalMaxDays, GrantRecertClass.Campaign90, false),
            _ => throw new InvalidOperationException("Unhandled grant class.")
        };

        // Effective maximum = min over the configured limits only: the optional class limit (a non-positive value fails closed
        // to one day) and the required tenant maximum.
        var classLimit = configured is { } d ? GrantTimeMath.DaysToSpan(d) : (TimeSpan?)null;
        if (classLimit is { } c && c <= TimeSpan.Zero)
        {
            classLimit = TimeSpan.FromDays(1);
        }

        return (GrantTimeMath.MinLimit(classLimit, TenantMaximum(kind))!.Value, recert, fourEyes);
    }

    // Declarative manifests are bounded by their own configured maximum; every other path by the tenant maximum.
    private TimeSpan TenantMaximum(GrantKind kind) =>
        kind == GrantKind.ManifestPolicy ? _validity.DeclarativeMaxDuration : _validity.MaxDuration;

    private static DateTimeOffset MaxValidTo(DateTimeOffset now, TimeSpan maxSpan) => GrantTimeMath.AddSaturating(now, maxSpan);

    private string? CustomerDisabled(GrantRequest r, GrantClass cls)
    {
        if (!r.CustomerRealm)
        {
            return null;
        }

        return cls switch
        {
            GrantClass.IndividualUnmasked when !_options.AllowCustomerUnmasked => "Individual Unmasked access is disabled for customer realms.",
            GrantClass.IndividualPrivileged when !_options.AllowCustomerPrivileged => "Individual privileged rights are disabled for customer realms.",
            _ => null
        };
    }

    private static string FormatSpan(TimeSpan s) => s.TotalHours < 24 * 2 ? $"{s.TotalHours:0} hours" : $"{s.TotalDays:0} days";
}
