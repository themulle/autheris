namespace Autheris.Domain.Options;

/// <summary>
/// Bounded grant validity options bound to <c>Gateway:Governance:Grants</c>.
/// Every duration uses ISO-8601 duration syntax (for example <c>P2W</c> or <c>P2M</c>).
/// </summary>
public sealed class GrantValidityOptions
{
    public const string SectionName = "Gateway:Governance:Grants";

    /// <summary>
    /// Maximum allowed ISO-8601 duration for consent requests and active grants (for example, <c>P5Y</c>). Required operator
    /// configuration with no compiled-in value; there is no built-in ceiling, so any positive duration (multi-year included)
    /// can be configured.
    /// </summary>
    public string MaxDuration { get; set; } = string.Empty;

    /// <summary>
    /// Default ISO-8601 duration applied when a consent request omits an explicit validity window or timestamp (for example, <c>P1Y</c>). Must not exceed <c>MaxDuration</c>.
    /// </summary>
    public string DefaultDuration { get; set; } = string.Empty;

    /// <summary>
    /// Maximum allowed ISO-8601 duration for declarative manifest grants (defaults to <c>MaxDuration</c> when omitted).
    /// </summary>
    public string? DeclarativeMaxDuration { get; set; }

    /// <summary>
    /// Test/unit default (multi-year) for service construction without dependency injection. It is NOT a production
    /// fallback: production requires explicit configuration and startup validation fails when <see cref="MaxDuration"/> is missing.
    /// </summary>
    public static GrantValidityOptions CreateDefault() => new()
    {
        DefaultDuration = "P1Y",
        MaxDuration = "P5Y",
        DeclarativeMaxDuration = "P5Y"
    };
}
