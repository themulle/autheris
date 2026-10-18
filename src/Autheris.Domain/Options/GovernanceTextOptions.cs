using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>
/// <c>Governance</c> (PLAN-GOV-158-INC1 decision 3): limits for owner-authored governance free text.
/// </summary>
public sealed class GovernanceTextOptions
{
    public const string SectionName = "Governance";

    /// <summary>Hard ceiling, equal to the width of the <c>CONSENTS.description</c> column (SEC-GOV158I1-11).</summary>
    public const int ConsentDescriptionCeiling = 2000;

    /// <summary>Maximum length of a consent description in UTF-16 code units after NFC and trim (default 500, range 50-2000).</summary>
    [Range(50, ConsentDescriptionCeiling)]
    public int ConsentDescriptionMaxLength { get; init; } = 500;
}
