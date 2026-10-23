using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Robin-100 rework v2 (WP-V2a), binding conditions SEC-R100-11 and SEC-R100-15 that can be checked on the test sources.
/// The scanners fail closed: a missing file, method or dictionary is a failure, never a vacuous pass.
/// </summary>
public sealed partial class Robin100V2aSecurityTests
{
    private const string RobinDir = "tests/Autheris.Tests.Parity/Robin";

    [GeneratedRegex(@"\[\s*""(?<method>[^""|]+)\|(?<channel>[^""|]*)\|(?<variant>[^""|]*)""\s*\]\s*=\s*""(?<id>[^""]+)""")]
    private static partial Regex GapKeyPattern();

    /// <summary>Gaps registered before rework v2 (functional deviations; none is a security cell).</summary>
    private static readonly string[] PreExistingGapMethods =
    [
        "Robin_Proactive_LikePrefix", "Robin_Proactive_IsNullAmount", "Robin_Proactive_IsNotNullRegion", "Robin_Proactive_GroupByQuarter",
        "Robin_130_HourBucketBerlinWinterSummer", "Robin_123_FederatedSameSourceSubqueryPart", "Robin_124_FederatedKeySetJoin",
    ];

    /// <summary>Non-security cells where a non-leaking rejection with another stable code may be a gap (plan 5.3 item 9, R88 excluded).</summary>
    private static readonly string[] ClosedCodeGapMethods =
    [
        "Robin_130_UnknownTimeZoneRejected", "Robin_222_TamperedCursorRejected", "Robin_30_UnknownTableSameCodeAsUnknownSchema", "Robin_30_UnknownColumnStableCode",
        "Robin_79_AnalyzeInvalidSqlStableCode", "Robin_Proactive_MultiStatementRejected", "Robin_Proactive_NegativeLimitRejected",
        "Robin_Proactive_AggregateErrorsStableCode", "Robin_Proactive_SetOperations", "Robin_Proactive_TypeMismatchStableCode",
        "Robin_302_OffsetBoundaryAndFirstZero", "Robin_Proactive_MalformedFilterStableCode", "Robin_128_InListOverCapStableCode",
    ];

    /// <summary>Non-security cells where a functional deviation (rows, order, shape) may be a gap (plan 5.4 candidates).</summary>
    private static readonly string[] FunctionalGapMethods =
    [
        "Robin_Proactive_NotInWithNullIsEmpty", "Robin_318_IntVariablesFirstOffset", "Robin_319_EmptyLogicalMembers", "Robin_Proactive_RowNumberOverPartition",
        "Robin_Proactive_NullSafeEquality", "Robin_Proactive_NotLikeAndNoMatchLike", "Robin_39_PageAboveCapIsRejectedOrSignalled",
        "Robin_130_MonthBucketBerlinYearEndAndDstHour", "Robin_Proactive_ArithmeticAndCast", "Robin_Proactive_StringFunctions", "Robin_112_CaseWhenBucketGroupBy",
        "Robin_Proactive_GroupByDatePartGraphQl", "Robin_Proactive_GroupByYearAndMonthWebSql",
        "Robin_Proactive_ODataFilterMatchesRest", "Robin_Proactive_OrderByNullPlacement", "Robin_Proactive_GraphQlVariablesAndDirectives",
        "Robin_Proactive_ScalarAndCorrelatedSubquery", "Robin_277_CteAndRecursiveGate", "Robin_116_UnmaskedColumnsOnMaskedTable", "Robin_163_GraphQlAliasesOnScopedTable",
    ];

    /// <summary>Security cells (denied, masked, scoped, gate, unauthenticated, no-mutation): never a gap, whatever the variant (SEC-R100-02/-11).</summary>
    private static readonly string[] SecurityMethods =
    [
        "Robin_27_FragmentsKeepMasking", "Robin_30_UnregisteredSourceTableDenied", "Robin_28_MaskedColumnThroughJoin", "Robin_28_AggregateOverMaskedColumn",
        "Robin_Proactive_UnauthenticatedRequestRejected", "Robin_Proactive_WriteStatementRejected",
        "Robin_27_MaskedValueMarkedInGraphQl", "Robin_28_FilterOnMaskedColumnDenied", "Robin_28_OrderByMaskedColumnDenied", "Robin_28_GroupByMaskedColumnDenied",
        "Robin_116_SelectStarOmitsOrMasksColumn", "Robin_163_RequiredFilterOnAggregate", "Robin_236_RequiredFilterRestOtherColumnNotEnough",
        "Robin_221_RequiredFilterInOrChainRejected", "Robin_236_RequiredFilterSatisfiedByInList",
    ];

    /// <summary>Variants of otherwise gappable methods that are security cells (the masked window, the recursive gate, the unfiltered scoped alias).</summary>
    private static readonly (string Method, string Variant)[] SecurityVariants =
    [
        ("Robin_Proactive_RowNumberOverPartition", "masked"),
        ("Robin_277_CteAndRecursiveGate", "gate"),
        ("Robin_163_GraphQlAliasesOnScopedTable", "all"),
    ];

    internal static IReadOnlyList<(string Method, string Channel, string Variant, string Id)> ReadKeys(string source) =>
        GapKeyPattern().Matches(source)
            .Select(m => (m.Groups["method"].Value, m.Groups["channel"].Value, m.Groups["variant"].Value, m.Groups["id"].Value))
            .ToList();

    internal static IReadOnlyList<string> Violations(string knownGapsSource)
    {
        var allowed = PreExistingGapMethods.Concat(ClosedCodeGapMethods).Concat(FunctionalGapMethods).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (method, channel, variant, id) in ReadKeys(knownGapsSource))
        {
            if (!Regex.IsMatch(id, @"^PARITY-GAP-\d{3}$"))
            {
                problems.Add($"{method}|{channel}|{variant}: malformed gap id '{id}'.");
            }

            if (SecurityMethods.Contains(method, StringComparer.Ordinal))
            {
                problems.Add($"{method}|{channel}|{variant}: security cell with a gap (SEC-R100-02/-11).");
            }
            else if (SecurityVariants.Any(s => s.Method == method && variant.Contains(s.Variant, StringComparison.Ordinal)))
            {
                problems.Add($"{method}|{channel}|{variant}: security variant with a gap (SEC-R100-11).");
            }
            else if (!allowed.Contains(method))
            {
                problems.Add($"{method}|{channel}|{variant}: not in the closed list of gappable cells (SEC-R100-11).");
            }
        }

        return problems;
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Robin100_KnownGapsOnlyInNonSecurityCellsWithStableCode()
    {
        var root = ParityIsolationArchitectureTests.FindRepoRoot();
        var gaps = Path.Combine(root, RobinDir, "RobinKnownGaps.cs");
        File.Exists(gaps).ShouldBeTrue("RobinKnownGaps.cs not found: the guard fails closed.");
        var source = File.ReadAllText(gaps);
        ReadKeys(source).Count.ShouldBeGreaterThan(0, "no gap key parsed: vacuous guard.");
        Violations(source).ShouldBeEmpty("SEC-R100-11: a gap outside the closed list of non-security cells.");
        Robin100ArchitectureTests.SecurityGapKeys(source).ShouldBeEmpty("SEC-R100-02: a denied/leak/masking/scope cell must never be a known gap.");

        // The code-gap cells reject through the shared helper: a 2xx for a must-reject request is a hard failure (never a gap), a 5xx is hard in RobinClient.
        var scenarios = File.ReadAllText(Path.Combine(root, RobinDir, "RobinScenarios.cs"));
        scenarios.ShouldContain("private static ChannelResult Denied(", Case.Sensitive, "the strict rejection helper is missing.");
        var helper = scenarios[scenarios.IndexOf("private static ChannelResult Denied(", StringComparison.Ordinal)..];
        helper = helper[..helper.IndexOf('}', StringComparison.Ordinal)];
        helper.ShouldContain("DeniedRequestSucceededException");

        // Negative self-checks.
        Violations("[\"Robin_28_MaskedColumnThroughJoin|WebSql|on_masked\"] = \"PARITY-GAP-060\",").Count.ShouldBe(1);
        Violations("[\"Robin_Proactive_RowNumberOverPartition|WebSql|masked\"] = \"PARITY-GAP-055\",").Count.ShouldBe(1);
        Violations("[\"Robin_Proactive_WriteStatementRejected|WebSql|\"] = \"PARITY-GAP-070\",").Count.ShouldBe(1);
        Violations("[\"Robin_Unknown_Method|WebSql|\"] = \"PARITY-GAP-070\",").Count.ShouldBe(1);
        Violations("[\"Robin_Proactive_SetOperations|WebSql|mismatch\"] = \"PARITY-GAP-057\",").ShouldBeEmpty();
        Violations("[\"Robin_Proactive_SetOperations|WebSql|mismatch\"] = \"GAP-57\",").Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Robin100_MaskedFragmentsDirectivesAndAliasesNeverLeakClearValue()
    {
        var dir = Path.Combine(ParityIsolationArchitectureTests.FindRepoRoot(), RobinDir);
        Directory.Exists(dir).ShouldBeTrue("Robin directory not found: the guard fails closed.");
        var scenarios = File.ReadAllText(Path.Combine(dir, "RobinScenarios.cs"));

        var fragments = MethodBody(scenarios, "Robin_27_FragmentsKeepMasking");
        foreach (var required in new[] { "fragment F on", "... on", "a: amount", "__typename", "RobinMaskedLeakScan.AssertNoClearAmount" })
        {
            fragments.ShouldContain(required, Case.Sensitive, $"v2-01 must cover '{required}' (SEC-R100-15).");
        }

        // The scan walks the whole body and throws the hard-failure type on any non-null masked key or clear fractional amount.
        var scan = scenarios[scenarios.IndexOf("internal static class RobinMaskedLeakScan", StringComparison.Ordinal)..];
        scan.ShouldContain("DeniedRequestSucceededException");
        scan.ShouldContain("ClearAmountToken.IsMatch(body)");
        scan.ShouldContain("ValueKind != System.Text.Json.JsonValueKind.Null");

        // v2-20 (directives and variables on parity_masked) lives in the WP-V2b part: once present it must use the same scan.
        foreach (var part in Directory.GetFiles(dir, "RobinScenarios.Wp*.cs"))
        {
            var source = File.ReadAllText(part);
            if (source.Contains("Robin_Proactive_GraphQlVariablesAndDirectives", StringComparison.Ordinal))
            {
                var body = MethodBody(source, "Robin_Proactive_GraphQlVariablesAndDirectives");
                body.ShouldContain("@include");
                body.ShouldContain("@skip");
                body.ShouldContain("RobinMaskedLeakScan.AssertNoClearAmount", Case.Sensitive, "v2-20 must scan the raw body for clear amounts (SEC-R100-15).");
            }
        }
    }

    private static string MethodBody(string source, string method)
    {
        var start = source.IndexOf("public Task " + method + "(", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, $"{method} not found: the guard fails closed.");
        var next = Regex.Match(source[(start + 10)..], @"\n    (\[Parity(Fact|Theory)\]|// v2-|// R\d+)");
        return next.Success ? source.Substring(start, next.Index + 10) : source[start..];
    }
}
