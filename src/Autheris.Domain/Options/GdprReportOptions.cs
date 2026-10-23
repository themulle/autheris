namespace Autheris.Domain.Options;

using System.ComponentModel.DataAnnotations;

/// <summary>MISC-11: limits of the GDPR Art. 15 disclosure report (<c>Gateway:GdprReport</c>).</summary>
public sealed class GdprReportOptions
{
    public const string SectionName = "Gateway:GdprReport";

    /// <summary>
    /// Hard cap of audit events evaluated per report. Hitting it marks the report incomplete (it never claims completeness without
    /// the full evidence). Default 100 000.
    /// </summary>
    [Range(1, 10_000_000)]
    public int MaxAuditEvents { get; init; } = 100_000;
}
