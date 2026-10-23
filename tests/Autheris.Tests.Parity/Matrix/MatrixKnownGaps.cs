namespace Autheris.Tests.Parity.Matrix;

/// <summary>
/// Cell-level known gaps of the matrix (strict xfail, D-5). Key: <c>capability|channel</c>. Every id must have a draft in section 5 of the
/// plan (guarded by the architecture test). 5xx and leaks never appear here (SEC-PAR-05). The gaps were observed on the SQLite
/// host, so they apply to SQLite only; other dialects get none until a nightly run observes one there.
/// </summary>
public static class MatrixKnownGaps
{
    private static readonly IReadOnlyDictionary<string, string> Gaps = new Dictionary<string, string>(StringComparer.Ordinal)
    {
    };

    public static IReadOnlyDictionary<string, string> All => Gaps;

    public static string? For(string capability, MatrixChannel channel, string dialect = "sqlite") =>
        dialect == "sqlite" && Gaps.TryGetValue($"{capability}|{channel}", out var id) ? id : null;
}
