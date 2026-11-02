namespace Autheris.Application.Governance.Grants;

using System;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Options-backed implementation of <see cref="IIndividualGrantLimits"/>.
/// Enforces fail-closed ceilings where options can only shorten built-in durations.
/// </summary>
public sealed class IndividualGrantLimitsService : IIndividualGrantLimits
{
    public TimeSpan RegularMax { get; }
    public TimeSpan UnmaskedMax { get; }
    public TimeSpan ServicePrincipalMax { get; }

    public IndividualGrantLimitsService(IOptions<IndividualGrantOptions> options)
        : this(options?.Value)
    {
    }

    public IndividualGrantLimitsService(IndividualGrantOptions? options = null)
    {
        var opts = options ?? new IndividualGrantOptions();
        RegularMax = Shorten(TimeSpan.FromDays(opts.RegularMaxDays), IndividualGrantLimits.DefaultRegularMax);
        UnmaskedMax = Shorten(TimeSpan.FromDays(opts.UnmaskedMaxDays), IndividualGrantLimits.DefaultUnmaskedMax);
        ServicePrincipalMax = Shorten(TimeSpan.FromDays(opts.ServicePrincipalMaxDays), IndividualGrantLimits.DefaultServicePrincipalMax);
    }

    public IndividualGrantLimitsService(TimeSpan regular, TimeSpan unmasked, TimeSpan servicePrincipal)
    {
        RegularMax = Shorten(regular, IndividualGrantLimits.DefaultRegularMax);
        UnmaskedMax = Shorten(unmasked, IndividualGrantLimits.DefaultUnmaskedMax);
        ServicePrincipalMax = Shorten(servicePrincipal, IndividualGrantLimits.DefaultServicePrincipalMax);
    }

    public DateTimeOffset EffectiveConsentValidTo(
        GranteeType granteeType,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ConsentEffect effect = ConsentEffect.Allow)
    {
        if (effect == ConsentEffect.Deny || validFrom == default)
        {
            return validTo;
        }

        TimeSpan max;
        switch (granteeType)
        {
            case GranteeType.User:
                max = RegularMax;
                break;
            case GranteeType.ServicePrincipal:
                max = ServicePrincipalMax;
                break;
            default:
                return validTo;
        }

        var cap = validFrom + max;
        return validTo < cap ? validTo : cap;
    }

    public DateTimeOffset? EffectiveProfileValidTo(
        MaskingPolicyMode mode,
        DateTimeOffset createdAt,
        DateTimeOffset? validTo)
    {
        if (mode != MaskingPolicyMode.Unmasked || createdAt == default)
        {
            return validTo;
        }

        var cap = createdAt + UnmaskedMax;
        return validTo is { } v && v < cap ? v : cap;
    }

    private static TimeSpan Shorten(TimeSpan configured, TimeSpan ceiling) =>
        configured <= TimeSpan.Zero ? TimeSpan.FromDays(1) : configured > ceiling ? ceiling : configured;
}
