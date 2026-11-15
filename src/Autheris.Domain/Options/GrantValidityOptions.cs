namespace Autheris.Domain.Options;

/// <summary>
/// Bounded grant validity options bound to <c>Gateway:Governance:Grants</c>.
/// Every duration uses ISO-8601 duration syntax (for example <c>P2W</c> or <c>P2M</c>).
/// </summary>
public sealed class GrantValidityOptions
{
    public const string SectionName = "Gateway:Governance:Grants";

    /// <summary>
    /// Maximum allowed ISO-8601 duration for consent requests and active grants (for example, <c>P2M</c>).
    /// </summary>
    public string MaxDuration { get; set; } = string.Empty;

    /// <summary>
    /// Default ISO-8601 duration applied when a consent request omits an explicit validity window or timestamp (for example, <c>P2W</c>).
    /// </summary>
    public string DefaultDuration { get; set; } = string.Empty;

    /// <summary>
    /// Maximum allowed ISO-8601 duration for declarative manifest grants (defaults to <see cref="MaxDuration"/> when omitted).
    /// </summary>
    public string? DeclarativeMaxDuration { get; set; }

    /// <summary>
    /// Built-in default values used by unit-level service construction without dependency injection.
    /// </summary>
    public static GrantValidityOptions CreateDefault() => new()
    {
        DefaultDuration = "P2W",
        MaxDuration = "P2M",
        DeclarativeMaxDuration = "P2M"
    };
}
