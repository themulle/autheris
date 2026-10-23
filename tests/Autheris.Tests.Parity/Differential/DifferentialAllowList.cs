namespace Autheris.Tests.Parity.Differential;

/// <summary>One documented intentional difference: the rule matches cases, the reason is a gap id or <c>documented: &lt;why&gt;</c>.</summary>
public sealed record AllowEntry(string Name, Func<DifferentialCase, bool> Matches, string Reason);

/// <summary>
/// Intentional differences between the product and the SQLite reference (D-16). A matching case may mismatch without failing; it is
/// recorded as an allowed difference. Every entry needs a reason that starts with <c>documented:</c> or is a <c>PARITY-GAP-NNN</c> id.
/// </summary>
public static class DifferentialAllowList
{
    public static IReadOnlyList<AllowEntry> Entries { get; } =
    [
        new(
            "graphql_rejects_type_invalid_aggregate",
            c => c.Channel == "GraphQl" && c.RejectedByTypedAggregation,
            "documented: the typed aggregation contract rejects an aggregate function that is invalid for the column type (AGGREGATION_INVALID_FUNCTION_FOR_TYPE); SQLite coerces the values"),
    ];

    public static AllowEntry? Find(DifferentialCase c) => Entries.FirstOrDefault(e => e.Matches(c));

    public static bool HasValidReason(AllowEntry e) =>
        !string.IsNullOrWhiteSpace(e.Name)
        && (e.Reason.StartsWith("documented: ", StringComparison.Ordinal) && e.Reason.Length > "documented: ".Length
            || KnownGapIdPattern.IsMatch(e.Reason));

    private static readonly System.Text.RegularExpressions.Regex KnownGapIdPattern = new(@"^PARITY-GAP-\d{3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
