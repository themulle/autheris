namespace Autheris.Domain.Options;

/// <summary>
/// Deployment-level tenancy interpretation (<c>Gateway:Tenancy:Mode</c>). <see cref="Multi"/> is the default and the safe
/// reading: a principal without a tenant claim is never treated as an unrestricted administrator.
/// </summary>
public enum TenancyMode
{
    /// <summary>Several tenants share the deployment. A principal without tenant claim gets no administrator view (fail closed).</summary>
    Multi = 0,

    /// <summary>One tenant owns the deployment. Tokens carry no tenant claim and a GovernanceAdmin sees the whole catalog.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Configuration value name fixed by the public option Gateway:Tenancy:Mode (Single | Multi).")]
    Single = 1
}

/// <summary>
/// <c>Gateway:Tenancy</c>. Single- versus multi-tenant is a deployment setting and is never inferred from a missing tenant claim.
/// </summary>
public sealed class TenancyOptions
{
    public const string SectionName = "Gateway:Tenancy";

    /// <summary>
    /// <c>Single</c> or <c>Multi</c> (names only, case-insensitive). Empty or unset means <see cref="TenancyMode.Multi"/>.
    /// Kept as text so an explicit setting can be told apart from the default and an unknown value fails start-up validation.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>True when the operator configured a mode explicitly.</summary>
    public bool IsExplicit => !string.IsNullOrWhiteSpace(Mode);

    /// <summary>True when <see cref="Mode"/> is unset or one of the two known names.</summary>
    public bool IsValid => !IsExplicit || TryParseMode(Mode, out _);

    /// <summary>The effective mode. Unset, empty and unknown values resolve to <see cref="TenancyMode.Multi"/>, never to <see cref="TenancyMode.Single"/>.</summary>
    public TenancyMode EffectiveMode => TryParseMode(Mode, out var mode) ? mode : TenancyMode.Multi;

    public static bool TryParseMode(string? value, out TenancyMode mode)
    {
        mode = TenancyMode.Multi;
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, nameof(TenancyMode.Multi), StringComparison.OrdinalIgnoreCase))
        {
            mode = TenancyMode.Multi;
            return true;
        }

        if (string.Equals(trimmed, nameof(TenancyMode.Single), StringComparison.OrdinalIgnoreCase))
        {
            mode = TenancyMode.Single;
            return true;
        }

        return false;
    }
}
