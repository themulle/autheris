namespace Autheris.Tests.Parity.Spec;

/// <summary>
/// Classification of block F cells. Gaps are functional deviations tracked by a PARITY-GAP draft (strict xfail); observed server
/// errors (5xx or leaks) are never gaps (SEC-PAR-05) and run in <c>Spec_ObservedServerErrors</c>, where they fail hard until fixed.
/// </summary>
public static class SpecKnownGaps
{
    /// <summary>Function name (lower case) => gap id.</summary>
    public static readonly IReadOnlyDictionary<string, string> Functions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["convert_timezone"] = "PARITY-GAP-019",
    };

    /// <summary>Function names (lower case) that currently answer 5xx or leak.</summary>
    public static readonly IReadOnlySet<string> ObservedFunctionServerErrors = new HashSet<string>(StringComparer.Ordinal)
    {
        // HTTP 502 SQL_PROVIDER_ERROR: advertised by the SQLite allowlist, but SQLite has no such function
        "stddev", "stddev_pop", "stddev_samp", "variance", "var_pop", "var_samp", "greatest", "least", "lpad", "rpad", "reverse", "char_length", "character_length",
    };

    /// <summary>GraphQL cell key <c>field.arg</c> => gap id.</summary>
    public static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);

    public static string? ForFunction(string name) => Functions.TryGetValue(name.ToLowerInvariant(), out var id) ? id : null;

    public static string? ForField(string key) => Fields.TryGetValue(key, out var id) ? id : null;
}
