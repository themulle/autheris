namespace Autheris.Domain.Model;

using System;

/// <summary>
/// Provides read-time clamp bounds for individual grants and access profiles.
/// Built-in ceilings: Regular User (180 days), Unmasked Profile (30 days), Service Principal (365 days).
/// Realm configurations can only shorten these limits, never extend them.
/// </summary>
public interface IIndividualGrantLimits
{
    TimeSpan RegularMax { get; }
    TimeSpan UnmaskedMax { get; }
    TimeSpan ServicePrincipalMax { get; }

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
