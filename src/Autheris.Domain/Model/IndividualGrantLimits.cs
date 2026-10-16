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

    private static long _regularTicks = DefaultRegularMax.Ticks;
    private static long _unmaskedTicks = DefaultUnmaskedMax.Ticks;
    private static long _servicePrincipalTicks = DefaultServicePrincipalMax.Ticks;

    public static TimeSpan RegularMax => TimeSpan.FromTicks(Interlocked.Read(ref _regularTicks));
    public static TimeSpan UnmaskedMax => TimeSpan.FromTicks(Interlocked.Read(ref _unmaskedTicks));
    public static TimeSpan ServicePrincipalMax => TimeSpan.FromTicks(Interlocked.Read(ref _servicePrincipalTicks));

    /// <summary>Applies realm-profile maxima. Values can only shorten the built-in ceilings; a non-positive value fails closed to one day.</summary>
    public static void Configure(TimeSpan regular, TimeSpan unmasked, TimeSpan servicePrincipal)
    {
        Interlocked.Exchange(ref _regularTicks, Shorten(regular, DefaultRegularMax));
        Interlocked.Exchange(ref _unmaskedTicks, Shorten(unmasked, DefaultUnmaskedMax));
        Interlocked.Exchange(ref _servicePrincipalTicks, Shorten(servicePrincipal, DefaultServicePrincipalMax));
    }

    public static void Reset() => Configure(DefaultRegularMax, DefaultUnmaskedMax, DefaultServicePrincipalMax);

    /// <summary>Effective expiry of a consent grant. A default <paramref name="validFrom"/> (never stored by a writer) is not clamped.</summary>
    public static DateTimeOffset EffectiveConsentValidTo(GranteeType granteeType, DateTimeOffset validFrom, DateTimeOffset validTo, ConsentEffect effect = ConsentEffect.Allow)
    {
        // A deny only restricts access; expiring it at read time would silently re-open the data (fail open).
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

    /// <summary>
    /// Effective expiry of an access profile. Only <see cref="MaskingPolicyMode.Unmasked"/> widens access (Default and Strict only
    /// tighten), so only it is bounded: <c>min(ValidTo ?? never, CreatedAt + unmasked max)</c>.
    /// </summary>
    public static DateTimeOffset? EffectiveProfileValidTo(MaskingPolicyMode mode, DateTimeOffset createdAt, DateTimeOffset? validTo)
    {
        if (mode != MaskingPolicyMode.Unmasked || createdAt == default)
        {
            return validTo;
        }

        var cap = createdAt + UnmaskedMax;
        return validTo is { } v && v < cap ? v : cap;
    }

    private static long Shorten(TimeSpan configured, TimeSpan ceiling) =>
        (configured <= TimeSpan.Zero ? TimeSpan.FromDays(1) : configured > ceiling ? ceiling : configured).Ticks;
}
