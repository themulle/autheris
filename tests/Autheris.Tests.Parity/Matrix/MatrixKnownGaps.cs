namespace Autheris.Tests.Parity.Matrix;

/// <summary>
/// Cell-level known gaps of the matrix (strict xfail, D-5). Key: <c>capability|channel</c>. Every id must have a draft in section 5 of the
/// plan (guarded by the architecture test). 5xx and leaks never appear here (SEC-PAR-05).
/// </summary>
public static class MatrixKnownGaps
{
    private static readonly IReadOnlyDictionary<string, string> Gaps = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["filter_eq_date|GraphQl"] = "PARITY-GAP-008",
        ["filter_range_date|GraphQl"] = "PARITY-GAP-012",
        ["group_date_part_standard_sql|WebSql"] = "PARITY-GAP-013",
        ["filter_in_empty|WebSql"] = "PARITY-GAP-020",
        ["agg_sum_text|WebSql"] = "PARITY-GAP-021",
        ["agg_avg_date|WebSql"] = "PARITY-GAP-021",
        ["agg_sum_bool|WebSql"] = "PARITY-GAP-021",
    };

    public static IReadOnlyDictionary<string, string> All => Gaps;

    public static string? For(string capability, MatrixChannel channel) =>
        Gaps.TryGetValue($"{capability}|{channel}", out var id) ? id : null;
}
