using System.Numerics;

namespace Autheris.Tests.Parity.Boundary;

/// <summary>Logical type a boundary value or a seed column has.</summary>
public enum ParityType
{
    Text,
    Whole,
    Numeric,
    Date,
    Timestamp,
    Bool,
    Null,
    List,
    Limit,
    Offset,
}

/// <summary>What a channel must do with a boundary value: succeed with the oracle result, reject with a stable 4xx, or either (clamping/coercion are both legitimate).</summary>
public enum Expectation
{
    Ok,
    Rejected,
    Either,
}

public enum ListKind
{
    Empty,
    One,
    Duplicates,
    WithNull,
    Many1000,
}

public sealed record BoundaryValue(string Id, ParityType Type, object? Value, Expectation Expected);

/// <summary>Seed column with its logical type (matches the catalog registration in <c>ParityHostFixture</c>).</summary>
public sealed record ParityColumn(string Name, ParityType Type);

/// <summary>
/// Shared boundary-value catalogue (D-10). Ordered and with unique ids. Every value has a decided <see cref="Expectation"/>.
/// Values are applied to every filter operand, sort key and paging parameter by <see cref="BoundaryCases"/>.
/// </summary>
public static class BoundaryValues
{
    public static readonly IReadOnlyList<ParityColumn> Columns =
    [
        new("id", ParityType.Whole),
        new("region_id", ParityType.Whole),
        new("region", ParityType.Text),
        new("product", ParityType.Text),
        new("qty", ParityType.Whole),
        new("amount", ParityType.Numeric),
        new("sale_date", ParityType.Date),
        new("sold_at", ParityType.Timestamp),
        new("is_return", ParityType.Bool),
        new("note", ParityType.Text),
    ];

    public static ParityColumn Column(string name) => Columns.Single(c => c.Name == name);

    public static readonly IReadOnlyList<BoundaryValue> All =
    [
        // null
        new("null.any", ParityType.Null, null, Expectation.Ok),

        // strings
        new("str.empty", ParityType.Text, string.Empty, Expectation.Ok),
        new("str.one_char", ParityType.Text, "N", Expectation.Ok),
        new("str.exact_north", ParityType.Text, "North", Expectation.Ok),
        new("str.case_lower", ParityType.Text, "north", Expectation.Ok),
        new("str.leading_space", ParityType.Text, " North", Expectation.Ok),
        new("str.quote", ParityType.Text, "O'Brien", Expectation.Ok),
        new("str.quote_only", ParityType.Text, "'", Expectation.Ok),
        new("str.double_quote", ParityType.Text, "\"", Expectation.Ok),
        new("str.backslash", ParityType.Text, "back\\slash", Expectation.Ok),
        new("str.percent", ParityType.Text, "50%", Expectation.Ok),
        new("str.underscore", ParityType.Text, "under_score", Expectation.Ok),
        new("str.emoji", ParityType.Text, "emoji \U0001F600", Expectation.Ok),
        new("str.unicode", ParityType.Text, "Ünïcödé 日本語", Expectation.Ok),
        new("str.bidi", ParityType.Text, "‮evil‬", Expectation.Ok),
        new("str.newline", ParityType.Text, "a\nb\r\nc", Expectation.Ok),
        new("str.nul", ParityType.Text, "a\0b", Expectation.Either),
        new("str.select_keyword", ParityType.Text, "SELECT * FROM x", Expectation.Ok),
        new("str.sql_drop", ParityType.Text, "North'; DROP TABLE parity_sales; --", Expectation.Ok),
        new("str.sql_or_true", ParityType.Text, "' OR '1'='1", Expectation.Ok),
        new("str.sql_union", ParityType.Text, "' UNION SELECT 1 --", Expectation.Ok),
        new("str.sql_comment", ParityType.Text, "/* c */ North", Expectation.Ok),
        new("str.col_id", ParityType.Text, "id", Expectation.Ok),
        new("str.col_note", ParityType.Text, "note", Expectation.Ok),
        new("str.col_upper", ParityType.Text, "ID", Expectation.Either),
        new("str.len1k", ParityType.Text, new string('a', 1024), Expectation.Ok),
        new("str.len64k", ParityType.Text, new string('a', 65536), Expectation.Either),

        // integers
        new("int.zero", ParityType.Whole, 0L, Expectation.Ok),
        new("int.one", ParityType.Whole, 1L, Expectation.Ok),
        new("int.neg_one", ParityType.Whole, -1L, Expectation.Ok),
        new("int.exact_qty", ParityType.Whole, 10L, Expectation.Ok),
        new("int.max", ParityType.Whole, 2147483647L, Expectation.Ok),
        new("int.min", ParityType.Whole, -2147483648L, Expectation.Ok),
        new("int.max_plus_1", ParityType.Whole, 2147483648L, Expectation.Either),
        new("int.long_max", ParityType.Whole, long.MaxValue, Expectation.Either),
        new("int.beyond_long", ParityType.Whole, BigInteger.Parse("9223372036854775808", System.Globalization.CultureInfo.InvariantCulture), Expectation.Either),

        // decimals
        new("dec.zero", ParityType.Numeric, 0m, Expectation.Ok),
        new("dec.exact", ParityType.Numeric, 50.50m, Expectation.Ok),
        new("dec.trailing_scale", ParityType.Numeric, 100.000m, Expectation.Ok),
        new("dec.negative_cent", ParityType.Numeric, -0.01m, Expectation.Ok),
        new("dec.tiny", ParityType.Numeric, 0.0000000001m, Expectation.Ok),
        new("dec.huge", ParityType.Numeric, 79228162514264337593543950335m, Expectation.Either),
        new("dec.nan_string", ParityType.Numeric, "NaN", Expectation.Either),
        new("dec.exponent_string", ParityType.Numeric, "1e3", Expectation.Either),

        // dates
        new("date.leap_2024_02_29", ParityType.Date, "2024-02-29", Expectation.Ok),
        new("date.dst_2024_03_31", ParityType.Date, "2024-03-31", Expectation.Ok),
        new("date.year_end", ParityType.Date, "2024-12-31", Expectation.Ok),
        new("date.min", ParityType.Date, "0001-01-01", Expectation.Ok),
        new("date.max", ParityType.Date, "9999-12-31", Expectation.Ok),
        new("date.invalid_2100_02_29", ParityType.Date, "2100-02-29", Expectation.Either),
        new("date.invalid_month", ParityType.Date, "2024-13-01", Expectation.Either),
        new("date.bad_format", ParityType.Date, "31.12.2024", Expectation.Either),
        new("date.empty", ParityType.Date, string.Empty, Expectation.Either),

        // timestamps and time zones
        new("ts.exact_dst_day", ParityType.Timestamp, "2024-03-31T00:30:00Z", Expectation.Ok),
        new("ts.leap_day", ParityType.Timestamp, "2024-02-29T23:59:59Z", Expectation.Ok),
        new("ts.berlin_new_year", ParityType.Timestamp, "2024-12-31T23:30:00Z", Expectation.Ok),
        new("ts.dst_ambiguous_hour", ParityType.Timestamp, "2024-10-27T01:30:00Z", Expectation.Ok),
        new("ts.offset_dst_gap", ParityType.Timestamp, "2024-03-31T02:30:00+02:00", Expectation.Either),
        new("ts.tz_invalid_zone", ParityType.Timestamp, "2024-03-31T00:30:00 Mars/Olympus", Expectation.Either),
        new("ts.bad_format", ParityType.Timestamp, "yesterday", Expectation.Either),

        // booleans
        new("bool.true", ParityType.Bool, true, Expectation.Ok),
        new("bool.false", ParityType.Bool, false, Expectation.Ok),

        // lists (generated per column by BoundaryCases)
        new("in.empty", ParityType.List, ListKind.Empty, Expectation.Either),
        new("in.one", ParityType.List, ListKind.One, Expectation.Ok),
        new("in.duplicates", ParityType.List, ListKind.Duplicates, Expectation.Ok),
        new("in.with_null", ParityType.List, ListKind.WithNull, Expectation.Ok),
        new("in.1000", ParityType.List, ListKind.Many1000, Expectation.Either),

        // paging: page size
        new("page.size_0", ParityType.Limit, 0L, Expectation.Ok),
        new("page.size_1", ParityType.Limit, 1L, Expectation.Ok),
        new("page.size_17", ParityType.Limit, 17L, Expectation.Ok),
        new("page.size_18", ParityType.Limit, 18L, Expectation.Ok),
        new("page.size_19", ParityType.Limit, 19L, Expectation.Ok),
        new("page.size_neg_1", ParityType.Limit, -1L, Expectation.Rejected),
        new("page.size_nan", ParityType.Limit, "abc", Expectation.Rejected),
        new("page.size_max_minus_0", ParityType.Limit, 1000000L, Expectation.Either),
        new("page.size_max_plus_1", ParityType.Limit, 2147483648L, Expectation.Either),
        new("page.size_beyond_long", ParityType.Limit, BigInteger.Parse("9223372036854775808", System.Globalization.CultureInfo.InvariantCulture), Expectation.Either),

        // paging: offset
        new("page.offset_0", ParityType.Offset, 0L, Expectation.Ok),
        new("page.offset_1", ParityType.Offset, 1L, Expectation.Ok),
        new("page.offset_17", ParityType.Offset, 17L, Expectation.Ok),
        new("page.offset_18", ParityType.Offset, 18L, Expectation.Ok),
        new("page.offset_19", ParityType.Offset, 19L, Expectation.Ok),
        new("page.offset_int_max", ParityType.Offset, 2147483647L, Expectation.Either),
        new("page.offset_neg_1", ParityType.Offset, -1L, Expectation.Rejected),
        new("page.offset_nan", ParityType.Offset, "abc", Expectation.Rejected),
        new("page.offset_int_max_plus_1", ParityType.Offset, 2147483648L, Expectation.Either),
        new("page.offset_beyond_long", ParityType.Offset, BigInteger.Parse("9223372036854775808", System.Globalization.CultureInfo.InvariantCulture), Expectation.Either),
    ];
}
