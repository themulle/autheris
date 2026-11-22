namespace Autheris.Application.Governance.Grants;

using System;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Options-backed implementation of <see cref="IIndividualGrantLimits"/>. Only operator-configured limits clamp; there are no
/// compiled-in ceilings (a missing value means no limit). Service principals share the user limits.
/// </summary>
public sealed class IndividualGrantLimitsService : IIndividualGrantLimits
{
    public TimeSpan? RegularMax { get; }
    public TimeSpan? UnmaskedMax { get; }

    public IndividualGrantLimitsService(IOptions<IndividualGrantOptions> options)
        : this(options?.Value)
    {
    }

    internal IndividualGrantLimitsService(IndividualGrantOptions? options = null)
        : this(Days(options?.RegularMaxDays), Days(options?.UnmaskedMaxDays))
    {
    }

    public IndividualGrantLimitsService(TimeSpan? regular, TimeSpan? unmasked)
    {
        RegularMax = IndividualGrantLimits.Normalize(regular);
        UnmaskedMax = IndividualGrantLimits.Normalize(unmasked);
    }

    public DateTimeOffset EffectiveConsentValidTo(
        GranteeType granteeType,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ConsentEffect effect = ConsentEffect.Allow) =>
        IndividualGrantLimits.ClampConsent(RegularMax, granteeType, validFrom, validTo, effect);

    public DateTimeOffset? EffectiveProfileValidTo(
        MaskingPolicyMode mode,
        DateTimeOffset createdAt,
        DateTimeOffset? validTo) =>
        IndividualGrantLimits.ClampProfile(UnmaskedMax, mode, createdAt, validTo);

    private static TimeSpan? Days(int? days) => days is { } d ? GrantTimeMath.DaysToSpan(d) : null;
}
