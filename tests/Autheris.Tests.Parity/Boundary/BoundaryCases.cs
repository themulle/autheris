using Autheris.Tests.Parity.Seed;

namespace Autheris.Tests.Parity.Boundary;

public enum BoundarySlot
{
    /// <summary>Equality filter with the value embedded as a literal.</summary>
    Eq,

    /// <summary>Equality filter with the value bound as a declared-query parameter / GraphQL variable.</summary>
    Param,
    In,
    Like,
    Sort,
    Limit,
    Offset,

    /// <summary>Malformed or unusual request (see <see cref="BoundaryRaw"/>).</summary>
    Raw,
}

/// <summary>One cell of the boundary sweep: a slot, a column (filter slots) and a catalogue value, or a raw request id.</summary>
public sealed record BoundaryCase(BoundarySlot Slot, ParityColumn? Column, BoundaryValue? Value, string? RawId = null)
{
    public string Id => $"{Slot.ToString().ToLowerInvariant()}.{Column?.Name ?? "none"}.{Value?.Id ?? RawId}";
}

/// <summary>Builds the case lists per channel (D-10). Case budget: at most 600 cells per channel.</summary>
public static class BoundaryCases
{
    public const int Budget = 600;

    public static readonly string[] ChannelNames = ["WebSql", "GraphQl", "Rest"];

    private static readonly Dictionary<ParityType, string[]> TypedColumns = new()
    {
        [ParityType.Text] = ["region", "note"],
        [ParityType.Whole] = ["qty", "id"],
        [ParityType.Numeric] = ["amount"],
        [ParityType.Date] = ["sale_date"],
        [ParityType.Timestamp] = ["sold_at"],
        [ParityType.Bool] = ["is_return"],
    };

    /// <summary>One representative column per type used by the cross-type invariant sweep.</summary>
    private static readonly string[] CrossColumns = ["region", "qty", "amount", "sale_date", "is_return"];

    public static bool Supports(string channel, BoundarySlot slot) =>
        channel != "Rest" || slot is BoundarySlot.Eq or BoundarySlot.In or BoundarySlot.Sort or BoundarySlot.Limit or BoundarySlot.Offset or BoundarySlot.Raw;

    /// <summary>Typed cases: a value is applied to the columns of its own type; the expectation of the catalogue decides the check.</summary>
    public static IReadOnlyList<BoundaryCase> For(string channel) =>
        Build(channel).Where(c => BoundaryKnownGaps.Classify(channel, c.Id).Kind != BoundaryClass.ObservedServerError).ToList();

    /// <summary>Every value on one column of every other type (equality literal only); asserts the invariant only.</summary>
    public static IReadOnlyList<BoundaryCase> CrossType(string channel) =>
        BuildCross(channel).Where(c => BoundaryKnownGaps.Classify(channel, c.Id).Kind != BoundaryClass.ObservedServerError).ToList();

    /// <summary>Cases that currently produce a 5xx or a leak (SEC-PAR-05). They stay hard failures; see section 5 of the plan.</summary>
    public static IReadOnlyList<BoundaryCase> ObservedServerErrors(string channel) =>
        Build(channel).Concat(BuildCross(channel))
            .Where(c => BoundaryKnownGaps.Classify(channel, c.Id).Kind == BoundaryClass.ObservedServerError)
            .ToList();

    public static IEnumerable<object[]> Rows(string channel, Func<string, IReadOnlyList<BoundaryCase>> source) =>
        source(channel).Select(c => new object[] { channel, c.Id });

    public static BoundaryCase Find(string channel, string id) =>
        Build(channel).Concat(BuildCross(channel)).First(c => c.Id == id);

    private static List<BoundaryCase> Build(string channel)
    {
        var cases = new List<BoundaryCase>();
        foreach (var v in BoundaryValues.All)
        {
            switch (v.Type)
            {
                case ParityType.Null:
                    foreach (var name in TypedColumns.Values.SelectMany(x => x).Distinct())
                    {
                        AddFilter(cases, channel, name, v);
                    }

                    break;
                case ParityType.List:
                    AddIf(cases, channel, new BoundaryCase(BoundarySlot.In, BoundaryValues.Column("region"), v));
                    AddIf(cases, channel, new BoundaryCase(BoundarySlot.In, BoundaryValues.Column("qty"), v));
                    break;
                case ParityType.Limit:
                    AddIf(cases, channel, new BoundaryCase(BoundarySlot.Limit, null, v));
                    break;
                case ParityType.Offset:
                    AddIf(cases, channel, new BoundaryCase(BoundarySlot.Offset, null, v));
                    break;
                default:
                    foreach (var name in TypedColumns[v.Type])
                    {
                        AddFilter(cases, channel, name, v);
                    }

                    if (v.Type == ParityType.Text)
                    {
                        AddIf(cases, channel, new BoundaryCase(BoundarySlot.Like, BoundaryValues.Column("note"), v));
                        AddIf(cases, channel, new BoundaryCase(BoundarySlot.Sort, null, v));
                    }

                    break;
            }
        }

        foreach (var raw in BoundaryRaw.Ids(channel))
        {
            cases.Add(new BoundaryCase(BoundarySlot.Raw, null, null, raw));
        }

        return cases;
    }

    private static List<BoundaryCase> BuildCross(string channel)
    {
        var cases = new List<BoundaryCase>();
        foreach (var v in BoundaryValues.All.Where(v => v.Type is not (ParityType.Null or ParityType.List or ParityType.Limit or ParityType.Offset)))
        {
            foreach (var name in CrossColumns)
            {
                var column = BoundaryValues.Column(name);
                if (column.Type != v.Type)
                {
                    cases.Add(new BoundaryCase(BoundarySlot.Eq, column, v));
                }
            }
        }

        return cases;
    }

    private static void AddFilter(List<BoundaryCase> cases, string channel, string column, BoundaryValue v)
    {
        var col = BoundaryValues.Column(column);
        AddIf(cases, channel, new BoundaryCase(BoundarySlot.Eq, col, v));
        AddIf(cases, channel, new BoundaryCase(BoundarySlot.Param, col, v));
    }

    private static void AddIf(List<BoundaryCase> cases, string channel, BoundaryCase c)
    {
        if (Supports(channel, c.Slot))
        {
            cases.Add(c);
        }
    }

    /// <summary>First non-null cell of a column, used to build IN lists that hit at least one seed row.</summary>
    internal static object FirstCell(ParityColumn column) =>
        ParitySeed.Sales.Select(s => BoundaryOracle.Cell(s, column.Name)).First(x => x is not null)!;
}
