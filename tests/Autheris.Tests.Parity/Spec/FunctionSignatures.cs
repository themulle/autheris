namespace Autheris.Tests.Parity.Spec;

/// <summary>Argument kinds of an advertised SQL function (D-12).</summary>
public enum FnArg
{
    None,
    Whole,
    Numeric,
    Text,
    Date,
    Timestamp,
    Boolean,
    Any,
}

/// <summary>
/// One row of the signature table: the argument types and a call over the seed (<c>parity_sales</c>, first row = id 1:
/// qty 10, amount 100.00, region North, product Widget, sale_date 2023-03-15, sold_at 2023-03-15T10:00:00Z, note 'plain').
/// <paramref name="Expected"/> is set only where the oracle is trivial and is compared with the first row.
/// </summary>
public sealed record FunctionSignature(string Name, FnArg[] Args, string Select, object? Expected = null, bool CheckValue = false)
{
    public string Sql => Select;
}

/// <summary>Signature table of every function advertised by <c>SqlFunctionAllowlists.Sqlite</c> (completeness is a test).</summary>
public static class FunctionSignatures
{
    private const string One = " FROM parity_sales WHERE id = 1";

    private const string Win = " FROM parity_sales ORDER BY id LIMIT 1";

    private const string Frame = "OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)";

    public static readonly IReadOnlyList<FunctionSignature> All =
    [
        // aggregates (whole seed table)
        Agg("count", [FnArg.Any], "COUNT(*)", 18),
        Agg("sum", [FnArg.Whole], "SUM(qty)", 69),
        Agg("avg", [FnArg.Numeric], "AVG(amount)", 125.43333333333333),
        Agg("min", [FnArg.Numeric], "MIN(amount)", 10),
        Agg("max", [FnArg.Numeric], "MAX(amount)", 400),
        Agg("stddev", [FnArg.Numeric], "STDDEV(amount)"),
        Agg("stddev_pop", [FnArg.Numeric], "STDDEV_POP(amount)"),
        Agg("stddev_samp", [FnArg.Numeric], "STDDEV_SAMP(amount)"),
        Agg("variance", [FnArg.Numeric], "VARIANCE(amount)"),
        Agg("var_pop", [FnArg.Numeric], "VAR_POP(amount)"),
        Agg("var_samp", [FnArg.Numeric], "VAR_SAMP(amount)"),
        Agg("group_concat", [FnArg.Text], "GROUP_CONCAT(product)"),
        Agg("total", [FnArg.Whole], "TOTAL(qty)", 69),

        // window functions (first row of the id order)
        Win1("row_number", [FnArg.None], "ROW_NUMBER() OVER (ORDER BY id)", 1),
        Win1("rank", [FnArg.None], "RANK() OVER (ORDER BY id)", 1),
        Win1("dense_rank", [FnArg.None], "DENSE_RANK() OVER (ORDER BY id)", 1),
        Win1("percent_rank", [FnArg.None], "PERCENT_RANK() OVER (ORDER BY id)", 0),
        Win1("cume_dist", [FnArg.None], "CUME_DIST() OVER (ORDER BY id)"),
        Win1("ntile", [FnArg.Whole], "NTILE(4) OVER (ORDER BY id)", 1),
        Win1("lag", [FnArg.Whole, FnArg.Whole], "LAG(qty, 1) OVER (ORDER BY id)", null, true),
        Win1("lead", [FnArg.Whole, FnArg.Whole], "LEAD(qty, 1) OVER (ORDER BY id)", 5),
        Win1("first_value", [FnArg.Whole], "FIRST_VALUE(qty) OVER (ORDER BY id)", 10),
        Win1("last_value", [FnArg.Whole], $"LAST_VALUE(qty) {Frame}", 4),
        Win1("nth_value", [FnArg.Whole, FnArg.Whole], $"NTH_VALUE(qty, 2) {Frame}", 5),

        // conditional
        One1("coalesce", [FnArg.Text, FnArg.Text], "COALESCE(note, 'x')", "plain"),
        One1("nullif", [FnArg.Whole, FnArg.Whole], "NULLIF(qty, 10)", null, true),
        One1("greatest", [FnArg.Whole, FnArg.Whole], "GREATEST(qty, 3)", 10),
        One1("least", [FnArg.Whole, FnArg.Whole], "LEAST(qty, 3)", 3),
        One1("ifnull", [FnArg.Text, FnArg.Text], "IFNULL(note, 'x')", "plain"),
        One1("iif", [FnArg.Boolean, FnArg.Text, FnArg.Text], "IIF(qty > 5, 'big', 'small')", "big"),

        // string
        One1("lower", [FnArg.Text], "LOWER(product)", "widget"),
        One1("upper", [FnArg.Text], "UPPER(product)", "WIDGET"),
        One1("trim", [FnArg.Text], "TRIM('  a ')", "a"),
        One1("ltrim", [FnArg.Text], "LTRIM('  a')", "a"),
        One1("rtrim", [FnArg.Text], "RTRIM('a  ')", "a"),
        One1("substring", [FnArg.Text, FnArg.Whole, FnArg.Whole], "SUBSTRING(product, 1, 3)", "Wid"),
        One1("substr", [FnArg.Text, FnArg.Whole, FnArg.Whole], "SUBSTR(product, 1, 3)", "Wid"),
        One1("concat", [FnArg.Text, FnArg.Text, FnArg.Text], "CONCAT(product, '-', region)", "Widget-North"),
        One1("length", [FnArg.Text], "LENGTH(product)", 6),
        One1("char_length", [FnArg.Text], "CHAR_LENGTH(product)", 6),
        One1("character_length", [FnArg.Text], "CHARACTER_LENGTH(product)", 6),
        One1("octet_length", [FnArg.Text], "OCTET_LENGTH(product)", 6),
        One1("replace", [FnArg.Text, FnArg.Text, FnArg.Text], "REPLACE(product, 'W', 'w')", "widget"),
        One1("position", [FnArg.Text, FnArg.Text], "POSITION('dg' IN product)", 3),
        One1("lpad", [FnArg.Text, FnArg.Whole, FnArg.Text], "LPAD(product, 8, '*')", "**Widget"),
        One1("rpad", [FnArg.Text, FnArg.Whole, FnArg.Text], "RPAD(product, 8, '*')", "Widget**"),
        One1("reverse", [FnArg.Text], "REVERSE(product)", "tegdiW"),
        One1("left", [FnArg.Text, FnArg.Whole], "LEFT(product, 3)", "Wid"),
        One1("right", [FnArg.Text, FnArg.Whole], "RIGHT(product, 3)", "get"),
        One1("instr", [FnArg.Text, FnArg.Text], "INSTR(product, 'dg')", 3),
        One1("unicode", [FnArg.Text], "UNICODE('A')", 65),
        One1("char", [FnArg.Whole], "CHAR(65)", "A"),

        // math
        One1("abs", [FnArg.Whole], "ABS(0 - qty)", 10),
        One1("round", [FnArg.Numeric, FnArg.Whole], "ROUND(amount, 1)", 100),
        One1("floor", [FnArg.Numeric], "FLOOR(amount)", 100),
        One1("ceil", [FnArg.Numeric], "CEIL(amount)", 100),
        One1("ceiling", [FnArg.Numeric], "CEILING(amount)", 100),
        One1("mod", [FnArg.Whole, FnArg.Whole], "MOD(qty, 3)", 1),
        One1("power", [FnArg.Whole, FnArg.Whole], "POWER(qty, 2)", 100),
        One1("sqrt", [FnArg.Whole], "SQRT(qty)", 3.1622776601683795),
        One1("exp", [FnArg.Whole], "EXP(qty - qty)", 1),
        One1("ln", [FnArg.Whole], "LN(qty)", 2.302585092994046),
        One1("log", [FnArg.Whole, FnArg.Whole], "LOG(10, qty)", 1),
        One1("log10", [FnArg.Whole], "LOG10(qty)", 1),
        One1("sign", [FnArg.Whole], "SIGN(0 - qty)", -1),
        One1("trunc", [FnArg.Numeric], "TRUNC(amount)", 100),
        One1("pi", [FnArg.None], "PI()", 3.141592653589793),

        // date / time
        One1("extract", [FnArg.Timestamp], "EXTRACT(YEAR FROM sold_at)", 2023),
        One1("cast", [FnArg.Any], "CAST(qty AS VARCHAR)", "10"),
        One1("current_date", [FnArg.None], "CURRENT_DATE"),
        One1("current_timestamp", [FnArg.None], "CURRENT_TIMESTAMP"),
        One1("convert_timezone", [FnArg.Text, FnArg.Text, FnArg.Timestamp], "CONVERT_TIMEZONE('UTC', 'Europe/Berlin', CAST(sold_at AS TIMESTAMP))"),
        One1("date", [FnArg.Date], "DATE(sold_at)", "2023-03-15"),
        One1("time", [FnArg.Timestamp], "TIME(sold_at)", "10:00:00"),
        One1("datetime", [FnArg.Timestamp], "DATETIME(sold_at)", "2023-03-15 10:00:00"),
        One1("julianday", [FnArg.Timestamp], "JULIANDAY(sold_at)"),
        One1("strftime", [FnArg.Text, FnArg.Timestamp], "STRFTIME('%Y', sold_at)", "2023"),
        One1("unixepoch", [FnArg.Timestamp], "UNIXEPOCH(sold_at)", 1678874400),
    ];

    private static FunctionSignature Agg(string name, FnArg[] args, string expr, object? expected = null) =>
        new(name, args, $"SELECT {expr} AS v FROM parity_sales", expected, expected is not null);

    private static FunctionSignature Win1(string name, FnArg[] args, string expr, object? expected = null, bool check = false) =>
        new(name, args, $"SELECT {expr} AS v{Win}", expected, check || expected is not null);

    private static FunctionSignature One1(string name, FnArg[] args, string expr, object? expected = null, bool check = false) =>
        new(name, args, $"SELECT {expr} AS v{One}", expected, check || expected is not null);

    public static FunctionSignature? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
