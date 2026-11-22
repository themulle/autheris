namespace Autheris.Domain.Model;

using System;

/// <summary>
/// Provides read-time clamp bounds for individual grants and access profiles.
/// There are no compiled-in ceilings: a <c>null</c> limit means the operator configured none for that class.
/// </summary>
public interface IIndividualGrantLimits
{
    TimeSpan? RegularMax { get; }
    TimeSpan? UnmaskedMax { get; }

    DateTimeOffset EffectiveConsentValidTo(
        GranteeType granteeType,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ConsentEffect effect = ConsentEffect.Allow);

    DateTimeOffset? EffectiveProfileValidTo(
        MaskingPolicyMode mode,
        DateTimeOffset createdAt,
        DateTimeOffset? validTo);
}
