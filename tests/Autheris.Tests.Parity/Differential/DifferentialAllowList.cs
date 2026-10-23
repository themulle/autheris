using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Invariant;

namespace Autheris.Tests.Parity.Differential;

/// <summary>One documented intentional difference: the rule matches cases, the reason is a gap id or <c>documented: &lt;why&gt;</c>.</summary>
/// <param name="ProductMatches">Predicate over the product answer; when set, a mismatch is only allowed if the answer satisfies it.</param>
public sealed record AllowEntry(string Name, Func<DifferentialCase, bool> Matches, string Reason, Func<ChannelResult, bool>? ProductMatches = null);

/// <summary>
/// Intentional differences between the product and the SQLite reference (D-16). A matching case may mismatch without failing; it is
/// recorded as an allowed difference. Every entry needs a reason that starts with <c>documented:</c> or is a <c>PARITY-GAP-NNN</c> id.
/// </summary>
public static class DifferentialAllowList
{
    public static IReadOnlyList<AllowEntry> Entries { get; } =
    [
        new(
            "typed_aggregation_rejects_type_invalid_aggregate",
            c => c.Channel is "GraphQl" or "WebSql" && c.RejectedByTypedAggregation,
            "documented: the typed aggregation contract rejects an aggregate function that is invalid for the column type (AGGREGATION_INVALID_FUNCTION_FOR_TYPE); SQLite coerces the values",
            // GraphQL answers HTTP 200 with errors[], WebSQL a 400 with the same code; only a rejection by that exact code is the contract, a wrong value or another code is a real mismatch
            r => ParityAssert.IsError(r) && r.Status < 500 && r.Code == "AGGREGATION_INVALID_FUNCTION_FOR_TYPE"),
    ];

    public static AllowEntry? Find(DifferentialCase c) => Entries.FirstOrDefault(e => e.Matches(c));

    /// <summary>The entry that allows this mismatch: the case matches and the product answer satisfies the entry's predicate.</summary>
    public static AllowEntry? Find(DifferentialCase c, ChannelResult product) =>
        Entries.FirstOrDefault(e => e.Matches(c) && (e.ProductMatches?.Invoke(product) ?? true));

    public static bool HasValidReason(AllowEntry e) =>
        !string.IsNullOrWhiteSpace(e.Name)
        && (e.Reason.StartsWith("documented: ", StringComparison.Ordinal) && e.Reason.Length > "documented: ".Length
            || KnownGapIdPattern.IsMatch(e.Reason));

    private static readonly System.Text.RegularExpressions.Regex KnownGapIdPattern = new(@"^PARITY-GAP-\d{3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
