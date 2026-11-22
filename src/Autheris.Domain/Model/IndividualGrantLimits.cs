namespace Autheris.Domain.Model;

using System;
using System.Threading;

/// <summary>
/// Read-time clamp of individual grants. The effective expiry of an individual grant is <c>min(ValidTo, ValidFrom + max)</c>
/// for every operator-configured limit; nothing is rewritten in storage. Service principals follow the same limits as users.
/// Group and role grants are standing and never clamped.
/// </summary>
public static class IndividualGrantLimits
{
    private static readonly IIndividualGrantLimits Unbounded = new ConfiguredLimits(null, null);
    private static IIndividualGrantLimits _current = Unbounded;

    /// <summary>The active <see cref="IIndividualGrantLimits"/> provider. Defaults to no limits.</summary>
    public static IIndividualGrantLimits Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? Unbounded);
    }

    public static TimeSpan? RegularMax => Current.RegularMax;
    public static TimeSpan? UnmaskedMax => Current.UnmaskedMax;

    /// <summary>Applies operator limits (<c>null</c> = none); a non-positive value fails closed to one day.</summary>
    public static void Configure(TimeSpan? regular, TimeSpan? unmasked, TimeSpan? _ = null)
    {
        Current = new ConfiguredLimits(regular, unmasked);
    }

    public static void Reset() => Current = Unbounded;

    /// <summary>Effective expiry of a consent grant. A default <paramref name="validFrom"/> (never stored by a writer) is not clamped.</summary>
    public static DateTimeOffset EffectiveConsentValidTo(GranteeType granteeType, DateTimeOffset validFrom, DateTimeOffset validTo, ConsentEffect effect = ConsentEffect.Allow) =>
        Current.EffectiveConsentValidTo(granteeType, validFrom, validTo, effect);

    /// <summary>
    /// Effective expiry of an access profile. Only <see cref="MaskingPolicyMode.Unmasked"/> widens access (Default and Strict only
    /// tighten), so only it is bounded, and only when an unmasked limit is configured.
    /// </summary>
    public static DateTimeOffset? EffectiveProfileValidTo(MaskingPolicyMode mode, DateTimeOffset createdAt, DateTimeOffset? validTo) =>
        Current.EffectiveProfileValidTo(mode, createdAt, validTo);

    /// <summary>Normalizes a configured limit: <c>null</c> stays unlimited, a non-positive value fails closed to one day.</summary>
    public static TimeSpan? Normalize(TimeSpan? configured) =>
        configured is { } v && v <= TimeSpan.Zero ? TimeSpan.FromDays(1) : configured;

    /// <summary>Shared clamp arithmetic of every provider.</summary>
    public static DateTimeOffset ClampConsent(TimeSpan? regular, GranteeType granteeType, DateTimeOffset validFrom, DateTimeOffset validTo, ConsentEffect effect)
    {
        if (effect == ConsentEffect.Deny || validFrom == default)
        {
            return validTo;
        }

        var max = granteeType is GranteeType.User or GranteeType.ServicePrincipal ? regular : null;
        if (max is null)
        {
            return validTo;
        }

        var cap = GrantTimeMath.AddSaturating(validFrom, max.Value);
        return validTo < cap ? validTo : cap;
    }

    /// <summary>Shared clamp arithmetic of every provider for access profiles.</summary>
    public static DateTimeOffset? ClampProfile(TimeSpan? unmasked, MaskingPolicyMode mode, DateTimeOffset createdAt, DateTimeOffset? validTo)
    {
        if (mode != MaskingPolicyMode.Unmasked || createdAt == default || unmasked is null)
        {
            return validTo;
        }

        var cap = GrantTimeMath.AddSaturating(createdAt, unmasked.Value);
        return validTo is { } v && v < cap ? v : cap;
    }

    private sealed class ConfiguredLimits(TimeSpan? regular, TimeSpan? unmasked) : IIndividualGrantLimits
    {
        public TimeSpan? RegularMax { get; } = Normalize(regular);
        public TimeSpan? UnmaskedMax { get; } = Normalize(unmasked);

        public DateTimeOffset EffectiveConsentValidTo(GranteeType granteeType, DateTimeOffset validFrom, DateTimeOffset validTo, ConsentEffect effect = ConsentEffect.Allow) =>
            ClampConsent(RegularMax, granteeType, validFrom, validTo, effect);

        public DateTimeOffset? EffectiveProfileValidTo(MaskingPolicyMode mode, DateTimeOffset createdAt, DateTimeOffset? validTo) =>
            ClampProfile(UnmaskedMax, mode, createdAt, validTo);
    }
}
