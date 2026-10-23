namespace Autheris.Tests.Parity.Robin;

/// <summary>
/// Cell-level known gaps of the Robin suite (strict xfail, D-5). Key: <c>CaseMethod|Channel|Variant</c> (variant empty when the case has none).
/// Every id must have a draft in section 5 of the plan (guarded by the architecture test). 5xx and leaks never appear here (SEC-PAR-05).
/// </summary>
public static class RobinKnownGaps
{
    private static readonly IReadOnlyDictionary<string, string> Gaps = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // --- WP-R1 ---
        ["Robin_Proactive_LikePrefix|Rest|"] = "PARITY-GAP-030",
        ["Robin_Proactive_IsNullAmount|Rest|"] = "PARITY-GAP-033",
        ["Robin_Proactive_IsNotNullRegion|Rest|"] = "PARITY-GAP-033",
        ["Robin_Proactive_NullBoolIsReturn|Rest|"] = "PARITY-GAP-033",
        ["Robin_Proactive_GroupByQuarter|GraphQl|"] = "PARITY-GAP-034",
        ["Robin_130_HourBucketBerlinWinterSummer|GraphQl|"] = "PARITY-GAP-035",
        // --- end WP-R1 ---
        // --- WP-R2 ---
        ["Robin_123_FederatedSameSourceSubqueryPart|WebSql|"] = "PARITY-GAP-050",
        ["Robin_124_FederatedKeySetJoin|WebSql|"] = "PARITY-GAP-050",
        // --- end WP-R2 ---
    };

    public static string? For(string caseMethod, string channel, string? variant) =>
        Gaps.TryGetValue($"{caseMethod}|{channel}|{variant}", out var id) ? id : null;
}
