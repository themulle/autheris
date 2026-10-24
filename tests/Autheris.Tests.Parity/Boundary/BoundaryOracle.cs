using System.Text.RegularExpressions;
using Autheris.Tests.Parity.Seed;

namespace Autheris.Tests.Parity.Boundary;

/// <summary>The expected outcome of one case. <see cref="Ids"/> is null when no oracle exists (Rejected / Either / unconstrained).</summary>
public sealed record BoundaryExpectation(Expectation Kind, IReadOnlyList<int>? Ids, bool Ordered, string? Code = null);

/// <summary>Expected row ids per case, computed in C# from the seed row literals (never by SQL).</summary>
public static class BoundaryOracle
{
    private static readonly IReadOnlyList<int> AllIds = ParitySeed.Sales.Select(s => s.Id).OrderBy(i => i).ToList();

    public static object? Cell(SaleRow s, string column) => column switch
    {
        "id" => (long)s.Id,
        "region_id" => s.RegionId is { } r ? (long)r : null,
        "region" => s.Region,
        "product" => s.Product,
        "qty" => s.Qty is { } q ? (long)q : null,
        "amount" => s.Amount,
        "sale_date" => s.SaleDate,
        "sold_at" => s.SoldAt,
        "is_return" => s.IsReturn,
        "note" => s.Note,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
    };

    public static BoundaryExpectation Expected(string channel, BoundaryCase c)
    {
        if (c.Slot == BoundarySlot.Raw)
        {
            return BoundaryRaw.Expected(channel, c.RawId!);
        }

        var v = c.Value!;
        if (c.Slot is BoundarySlot.Eq or BoundarySlot.Like && channel is "WebSql" or "Rest" && IsRejectedLiteral(v))
        {
            // Documented rejection (#296): a string literal with a comment marker or backslash is not accepted inline; a parameter is the supported path.
            return new(Expectation.Rejected, null, true, "SQL_LITERAL_NOT_PERMITTED");
        }

        switch (c.Slot)
        {
            case BoundarySlot.Eq when channel == "Rest" && v.Type == ParityType.Null:
                // #324 (GAP-033): REST `eq null` is an IS NULL test by contract (the REST filter grammar has no other way to ask for NULL);
                // WebSQL and GraphQL keep the three-valued `= NULL` (#300).
                return v.Expected == Expectation.Ok ? new(Expectation.Ok, Ids(c.Column!, x => x is null), true) : new(v.Expected, null, true);
            case BoundarySlot.Eq:
            case BoundarySlot.Param:
                return v.Expected == Expectation.Ok && Matches(c.Column!, v) ? new(Expectation.Ok, Ids(c.Column!, x => IsEqual(x, v.Value)), true) : new(v.Expected, null, true);
            case BoundarySlot.In:
                return v.Expected == Expectation.Ok
                    ? new(Expectation.Ok, Ids(c.Column!, x => x is not null && ListFor((ListKind)v.Value!, c.Column!).Any(e => IsEqual(x, e))), true)
                    : new(v.Expected, null, true);
            case BoundarySlot.Like:
                return v.Expected == Expectation.Ok
                    ? new(Expectation.Ok, Ids(c.Column!, x => x is string s && LikeMatches(channel, s, (string)v.Value!)), true)
                    : new(v.Expected, null, true);
            case BoundarySlot.Sort:
                return SortExpectation(v);
            case BoundarySlot.Limit:
                return v.Expected == Expectation.Ok ? new(Expectation.Ok, AllIds.Take(checked((int)(long)v.Value!)).ToList(), true) : new(v.Expected, null, true);
            case BoundarySlot.Offset:
                return v.Expected == Expectation.Ok ? new(Expectation.Ok, AllIds.Skip(checked((int)(long)v.Value!)).ToList(), true) : new(v.Expected, null, true);
            default:
                throw new InvalidOperationException($"No oracle for slot {c.Slot}.");
        }
    }

    public static IReadOnlyList<object?> ListFor(ListKind kind, ParityColumn column)
    {
        var first = BoundaryCases.FirstCell(column);
        return kind switch
        {
            ListKind.Empty => [],
            ListKind.One => [first],
            ListKind.Duplicates => [first, first, first],
            ListKind.WithNull => new object?[] { first, null },
            _ => Enumerable.Range(0, 1000).Select(i => i == 0 ? first : column.Type == ParityType.Whole ? (object)(100000L + i) : $"filler_{i}").ToList(),
        };
    }

    private static bool IsRejectedLiteral(BoundaryValue v) =>
        v.Value is string text && (text.Contains("--", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal) || text.Contains('\\', StringComparison.Ordinal));

    private static BoundaryExpectation SortExpectation(BoundaryValue v)
    {
        var key = (string)v.Value!;
        if (v.Expected == Expectation.Either || key.Length == 0)
        {
            // An empty sort key may legitimately mean "no ordering".
            return new(Expectation.Either, null, true);
        }

        if (BoundaryValues.Columns.Any(c => c.Name == key))
        {
            return new(Expectation.Ok, AllIds, key == "id");
        }

        return new(Expectation.Rejected, null, true);
    }

    /// <summary>The value is applicable to the column's type, so the oracle can decide equality.</summary>
    private static bool Matches(ParityColumn column, BoundaryValue v) =>
        v.Type == ParityType.Null || v.Type == column.Type;

    private static IReadOnlyList<int> Ids(ParityColumn column, Func<object?, bool> predicate) =>
        ParitySeed.Sales.Where(s => predicate(Cell(s, column.Name))).Select(s => s.Id).OrderBy(i => i).ToList();

    private static bool IsEqual(object? cell, object? value)
    {
        if (cell is null || value is null)
        {
            return false;
        }

        return cell switch
        {
            string s => value is string t && string.Equals(s, t, StringComparison.Ordinal),
            long l => value is long m && l == m,
            decimal d => value is decimal e && d == e,
            bool b => value is bool c && b == c,
            _ => false,
        };
    }

    private static bool LikeMatches(string channel, string cell, string pattern)
    {
        if (channel == "GraphQl")
        {
            return cell.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + string.Concat(pattern.Select(ch => ch switch
        {
            '%' => ".*",
            '_' => ".",
            _ => Regex.Escape(ch.ToString()),
        })) + "$";
        return Regex.IsMatch(cell, regex, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    }
}
