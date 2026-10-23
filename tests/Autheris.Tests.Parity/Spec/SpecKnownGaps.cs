namespace Autheris.Tests.Parity.Spec;

/// <summary>
/// Classification of block F cells. Gaps are functional deviations tracked by a PARITY-GAP draft (strict xfail); observed server
/// errors (5xx or leaks) are never gaps (SEC-PAR-05). Functions SQLite cannot run are expected-unsupported cells.
/// </summary>
public static class SpecKnownGaps
{
    /// <summary>Function name (lower case) => gap id.</summary>
    public static readonly IReadOnlyDictionary<string, string> Functions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["convert_timezone"] = "PARITY-GAP-019",
        ["left"] = "PARITY-GAP-022",
        ["right"] = "PARITY-GAP-022",
        ["extract"] = "PARITY-GAP-022",
    };

    /// <summary>
    /// Function names (lower case) that SQLite cannot execute and that the SQLite allowlist therefore does not advertise. The gateway
    /// rejects a call fail-closed with <see cref="UnsupportedFunctionStatus"/> / <see cref="UnsupportedFunctionCode"/> (never a 5xx);
    /// they run in <c>Spec_UnsupportedFunction</c> as expected-unsupported cells.
    /// </summary>
    public static readonly IReadOnlySet<string> UnsupportedFunctions = new HashSet<string>(StringComparer.Ordinal)
    {
        "stddev", "stddev_pop", "stddev_samp", "variance", "var_pop", "var_samp", "greatest", "least", "lpad", "rpad", "reverse", "char_length", "character_length",
    };

    public const int UnsupportedFunctionStatus = 403;

    public const string UnsupportedFunctionCode = "SQL_POLICY_VIOLATION";

    /// <summary>GraphQL cell key <c>field.arg</c> => gap id.</summary>
    public static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);

    public static string? ForFunction(string name) => Functions.TryGetValue(name.ToLowerInvariant(), out var id) ? id : null;

    public static string? ForField(string key) => Fields.TryGetValue(key, out var id) ? id : null;
}
