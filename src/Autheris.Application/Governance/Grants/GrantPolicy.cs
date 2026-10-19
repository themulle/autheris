namespace Autheris.Application.Governance.Grants;

using System;
using System.Linq;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// WP-SC-16 time-bound individual grant policy. Individual grants (<see cref="GranteeType.User"/>, <see cref="GranteeType.ServicePrincipal"/>)
/// always need an explicit <c>ValidTo</c> within the maximum of their class; group and role grants are standing and unaffected.
/// Renewal is a new approval, never by the grantee. Any unexpected state denies.
/// </summary>
public sealed class GrantPolicy(IOptions<IndividualGrantOptions>? options = null) : IGrantPolicy
{
    private readonly IndividualGrantOptions _options = options?.Value ?? new IndividualGrantOptions();

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
        if (!Enum.IsDefined(r.GranteeType))
        {
            return GrantDecision.Deny(GrantErrorCodes.Indeterminate, "Unknown grantee type.");
        }

        if (r.Effect == ConsentEffect.Deny && r.Kind != GrantKind.BreakGlass)
        {
            return new GrantDecision(true, null, false, GrantRecertClass.None, GrantClass.Standing, "Deny grants only restrict access and have no maximum.");
        }

        if (r.GranteeType is GranteeType.Group or GranteeType.Role && r.Kind != GrantKind.BreakGlass)
        {
            return new GrantDecision(true, null, false, GrantRecertClass.None, GrantClass.Standing, "Standing group/role grant.");
        }

        var cls = Classify(r);
        var (maxSpan, recert, fourEyes) = Limits(cls);
        var disabled = CustomerDisabled(r, cls);
        if (disabled != null)
        {
            return GrantDecision.Deny(GrantErrorCodes.DisabledForRealm, disabled, cls);
        }

        var maxValidTo = r.Now + maxSpan;

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

        // Rule 2: maximum duration per class (a realm profile can only shorten it).
        if (r.ValidTo.Value > maxValidTo)
        {
            return GrantDecision.Deny(GrantErrorCodes.ExceedsMaximum,
                $"The requested expiry exceeds the maximum of {FormatSpan(maxSpan)} for this grant class.", cls, maxValidTo);
        }

        if ((cls is GrantClass.IndividualUnmasked or GrantClass.BreakGlass or GrantClass.IndividualPrivileged) &&
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
            if (cls is GrantClass.BreakGlass)
            {
                needed = 1; // second approval may follow after the fact within 24 h; at least one independent approver is required
            }

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
        if (r.Kind == GrantKind.BreakGlass)
        {
            return GrantClass.BreakGlass;
        }

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

    private (TimeSpan Max, GrantRecertClass Recert, bool FourEyes) Limits(GrantClass cls) => cls switch
    {
        GrantClass.Regular => (Days(_options.RegularMaxDays, IndividualGrantCeilings.RegularDays), GrantRecertClass.Campaign365, false),
        GrantClass.CustomerRegular => (Days(_options.CustomerRegularMaxDays, IndividualGrantCeilings.CustomerRegularDays), GrantRecertClass.CustomerAdminAttestation, false),
        GrantClass.Sensitive => (Days(_options.SensitiveMaxDays, IndividualGrantCeilings.SensitiveDays), GrantRecertClass.Campaign90, true),
        GrantClass.IndividualUnmasked => (Days(_options.UnmaskedMaxDays, IndividualGrantCeilings.UnmaskedDays), GrantRecertClass.Campaign90, true),
        GrantClass.IndividualPrivileged => (Days(_options.PrivilegedMaxDays, IndividualGrantCeilings.PrivilegedDays), GrantRecertClass.Campaign90, true),
        GrantClass.BreakGlass => (Hours(_options.BreakGlassMaxHours, IndividualGrantCeilings.BreakGlassHours), GrantRecertClass.Campaign90, true),
        GrantClass.ServicePrincipal => (Days(_options.ServicePrincipalMaxDays, IndividualGrantCeilings.ServicePrincipalDays), GrantRecertClass.Campaign90, false),
        _ => throw new InvalidOperationException("Unhandled grant class.")
    };

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

    // A realm profile can only shorten: clamp into [1, ceiling]; a non-positive value fails closed to the shortest span.
    private static TimeSpan Days(int configured, int ceiling) => TimeSpan.FromDays(Math.Clamp(configured, 1, ceiling));

    private static TimeSpan Hours(int configured, int ceiling) => TimeSpan.FromHours(Math.Clamp(configured, 1, ceiling));

    private static string FormatSpan(TimeSpan s) => s.TotalHours < 24 * 2 ? $"{s.TotalHours:0} hours" : $"{s.TotalDays:0} days";
}
