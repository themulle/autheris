namespace Autheris.Domain.Model;

using System;
using System.Threading;

/// <summary>
/// WP-SC-16 rule 5: read-time clamp of legacy individual grants. The effective expiry of an individual grant is
/// <c>min(ValidTo, ValidFrom + max)</c>; nothing is rewritten in storage. The class of a stored grant is not known at read time,
/// so the clamp uses the largest class maximum as a backstop (regular data for users, 365 days for service principals); the write
/// paths enforce the exact class maximum through the grant policy. Group and role grants are standing and never clamped.
/// </summary>
public static class IndividualGrantLimits
{
    public static readonly TimeSpan DefaultRegularMax = TimeSpan.FromDays(180);
    public static readonly TimeSpan DefaultUnmaskedMax = TimeSpan.FromDays(30);
    public static readonly TimeSpan DefaultServicePrincipalMax = TimeSpan.FromDays(365);

    private static readonly IIndividualGrantLimits BuiltIn = new DefaultBuiltInLimits();
    private static IIndividualGrantLimits _current = BuiltIn;

    /// <summary>
    /// The active <see cref="IIndividualGrantLimits"/> provider. Defaults to fail-closed built-in defaults.
    /// </summary>
    public static IIndividualGrantLimits Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? BuiltIn);
    }

    public static TimeSpan RegularMax => Current.RegularMax;
    public static TimeSpan UnmaskedMax => Current.UnmaskedMax;
    public static TimeSpan ServicePrincipalMax => Current.ServicePrincipalMax;

    /// <summary>Applies realm-profile maxima. Values can only shorten the built-in ceilings; a non-positive value fails closed to one day.</summary>
    public static void Configure(TimeSpan regular, TimeSpan unmasked, TimeSpan servicePrincipal)
    {
        Current = new DelegatingConfiguredLimits(regular, unmasked, servicePrincipal);
    }

    public static void Reset() => Current = BuiltIn;

    /// <summary>Effective expiry of a consent grant. A default <paramref name="validFrom"/> (never stored by a writer) is not clamped.</summary>
    public static DateTimeOffset EffectiveConsentValidTo(GranteeType granteeType, DateTimeOffset validFrom, DateTimeOffset validTo, ConsentEffect effect = ConsentEffect.Allow) =>
        Current.EffectiveConsentValidTo(granteeType, validFrom, validTo, effect);

    /// <summary>
    /// Effective expiry of an access profile. Only <see cref="MaskingPolicyMode.Unmasked"/> widens access (Default and Strict only
    /// tighten), so only it is bounded: <c>min(ValidTo ?? never, CreatedAt + unmasked max)</c>.
    /// </summary>
    public static DateTimeOffset? EffectiveProfileValidTo(MaskingPolicyMode mode, DateTimeOffset createdAt, DateTimeOffset? validTo) =>
        Current.EffectiveProfileValidTo(mode, createdAt, validTo);

    private sealed class DefaultBuiltInLimits : IIndividualGrantLimits
    {
        public TimeSpan RegularMax => DefaultRegularMax;
        public TimeSpan UnmaskedMax => DefaultUnmaskedMax;
        public TimeSpan ServicePrincipalMax => DefaultServicePrincipalMax;

        public DateTimeOffset EffectiveConsentValidTo(GranteeType granteeType, DateTimeOffset validFrom, DateTimeOffset validTo, ConsentEffect effect = ConsentEffect.Allow)
        {
            if (effect == ConsentEffect.Deny || validFrom == default) return validTo;
            TimeSpan max = granteeType switch
            {
                GranteeType.User => DefaultRegularMax,
                GranteeType.ServicePrincipal => DefaultServicePrincipalMax,
                _ => TimeSpan.Zero,
            };
            if (max == TimeSpan.Zero) return validTo;
            var cap = validFrom + max;
            return validTo < cap ? validTo : cap;
        }

        public DateTimeOffset? EffectiveProfileValidTo(MaskingPolicyMode mode, DateTimeOffset createdAt, DateTimeOffset? validTo)
        {
            if (mode != MaskingPolicyMode.Unmasked || createdAt == default) return validTo;
            var cap = createdAt + DefaultUnmaskedMax;
            return validTo is { } v && v < cap ? v : cap;
        }
    }

    private sealed class DelegatingConfiguredLimits : IIndividualGrantLimits
    {
        public TimeSpan RegularMax { get; }
        public TimeSpan UnmaskedMax { get; }
        public TimeSpan ServicePrincipalMax { get; }

        public DelegatingConfiguredLimits(TimeSpan regular, TimeSpan unmasked, TimeSpan servicePrincipal)
        {
            RegularMax = Shorten(regular, DefaultRegularMax);
            UnmaskedMax = Shorten(unmasked, DefaultUnmaskedMax);
            ServicePrincipalMax = Shorten(servicePrincipal, DefaultServicePrincipalMax);
        }

        public DateTimeOffset EffectiveConsentValidTo(GranteeType granteeType, DateTimeOffset validFrom, DateTimeOffset validTo, ConsentEffect effect = ConsentEffect.Allow)
        {
            if (effect == ConsentEffect.Deny || validFrom == default) return validTo;
            TimeSpan max = granteeType switch
            {
                GranteeType.User => RegularMax,
                GranteeType.ServicePrincipal => ServicePrincipalMax,
                _ => TimeSpan.Zero,
            };
            if (max == TimeSpan.Zero) return validTo;
            var cap = validFrom + max;
            return validTo < cap ? validTo : cap;
        }

        public DateTimeOffset? EffectiveProfileValidTo(MaskingPolicyMode mode, DateTimeOffset createdAt, DateTimeOffset? validTo)
        {
            if (mode != MaskingPolicyMode.Unmasked || createdAt == default) return validTo;
            var cap = createdAt + UnmaskedMax;
            return validTo is { } v && v < cap ? v : cap;
        }

        private static TimeSpan Shorten(TimeSpan configured, TimeSpan ceiling) =>
            configured <= TimeSpan.Zero ? TimeSpan.FromDays(1) : configured > ceiling ? ceiling : configured;
    }
}
