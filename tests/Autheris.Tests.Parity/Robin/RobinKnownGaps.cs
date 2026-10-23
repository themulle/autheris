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
        ["Robin_Proactive_GroupByQuarter|GraphQl|"] = "PARITY-GAP-034",
        ["Robin_130_HourBucketBerlinWinterSummer|GraphQl|"] = "PARITY-GAP-035",
        // --- end WP-R1 ---
        // --- WP-R2 ---
        ["Robin_123_FederatedSameSourceSubqueryPart|WebSql|"] = "PARITY-GAP-050",
        // --- end WP-R2 ---
        // --- WP-V2a ---
        ["Robin_Proactive_NotInWithNullIsEmpty|GraphQl|"] = "PARITY-GAP-051",
        ["Robin_318_IntVariablesFirstOffset|GraphQl|five_ten"] = "PARITY-GAP-052",
        ["Robin_318_IntVariablesFirstOffset|GraphQl|nulls"] = "PARITY-GAP-052",
        ["Robin_318_IntVariablesFirstOffset|GraphQl|zero"] = "PARITY-GAP-052",
        ["Robin_319_EmptyLogicalMembers|GraphQl|or_empty_member"] = "PARITY-GAP-053",
        ["Robin_302_OffsetBoundaryAndFirstZero|Rest|offset_over"] = "PARITY-GAP-054",
        ["Robin_Proactive_AggregateErrorsStableCode|WebSql|unknown_group_column"] = "PARITY-GAP-055",
        ["Robin_Proactive_TypeMismatchStableCode|Rest|dangling_filter"] = "PARITY-GAP-056",
        ["Robin_39_PageAboveCapIsRejectedOrSignalled|GraphQl|"] = "PARITY-GAP-057",
        // --- end WP-V2a ---
        // --- WP-V2b ---
        ["Robin_Proactive_MalformedFilterStableCode|Rest|unbalanced_filter"] = "PARITY-GAP-070",
        ["Robin_Proactive_NegativeLimitRejected|GraphQl|"] = "PARITY-GAP-071",
        // --- end WP-V2b ---
    };

    public static string? For(string caseMethod, string channel, string? variant) =>
        Gaps.TryGetValue($"{caseMethod}|{channel}|{variant}", out var id) ? id : null;
}
