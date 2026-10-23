namespace Autheris.Tests.Parity.Robin;

/// <summary>
/// Cell-level known gaps of the Robin suite (strict xfail, D-5). Key: <c>CaseMethod|Channel|Variant</c> (variant empty when the case has none).
/// Every id must have a draft in section 5 of the plan (guarded by the architecture test). 5xx and leaks never appear here (SEC-PAR-05).
/// </summary>
public static class RobinKnownGaps
{
    private static readonly IReadOnlyDictionary<string, string> Gaps = new Dictionary<string, string>(StringComparer.Ordinal)
    {
    };

    public static string? For(string caseMethod, string channel, string? variant) =>
        Gaps.TryGetValue($"{caseMethod}|{channel}|{variant}", out var id) ? id : null;
}
