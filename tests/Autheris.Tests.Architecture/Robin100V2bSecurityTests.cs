using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Robin-100 rework v2, work package V2b: binding conditions SEC-R100-12, -13, -14 and -17 checked on the test sources.
/// The scanners fail closed (a missing file or method is a failure, never a vacuous pass).
/// </summary>
public sealed class Robin100V2bSecurityTests
{
    private static string RobinDir => Path.Combine(ParityIsolationArchitectureTests.FindRepoRoot(), "tests", "Autheris.Tests.Parity", "Robin");

    private static string Read(string file)
    {
        var path = Path.Combine(RobinDir, file);
        File.Exists(path).ShouldBeTrue($"{file} not found: the guard fails closed.");
        return File.ReadAllText(path);
    }

    /// <summary>The source text of one scenario method: from its declaration to the next attribute line or the end of the class.</summary>
    private static string MethodBody(string source, string method)
    {
        var start = source.IndexOf($"public Task {method}(", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, $"{method} not found: the guard fails closed.");
        var next = Regex.Match(source[start..], @"\n    \[Parity(Fact|Theory)\]");
        return next.Success ? source.Substring(start, next.Index) : source[start..];
    }

    private static string[] GapKeysOf(string method) =>
        Regex.Matches(Read("RobinKnownGaps.cs"), @"\[\s*""(?<key>[^""]+)""\s*\]\s*=")
            .Select(m => m.Groups["key"].Value)
            .Where(k => k.StartsWith(method + "|", StringComparison.Ordinal))
            .ToArray();

    /// <summary>SEC-R100-12: the anonymous client exists once, is reached only through the 401 assertion helper, and that helper only asserts 401.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public void Robin100_UnauthenticatedClientUsedOnlyForUnauthorizedAssertions()
    {
        var root = ParityIsolationArchitectureTests.FindRepoRoot();
        // No other file of any test project builds or calls the anonymous client.
        var offenders = Directory.GetFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.EndsWith("Robin100V2bSecurityTests.cs", StringComparison.Ordinal)
                        && !f.EndsWith("Robin100SecurityTests.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("CreateUnauthenticatedClient(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        offenders.ShouldBe(["RobinClient.cs"], "SEC-R100-12: only RobinClient.cs may reference the anonymous client.");

        var client = Read("RobinClient.cs");
        Regex.Count(client, @"CreateUnauthenticatedClient\(").ShouldBe(2, "the definition and the single call in AssertUnauthorizedAsync.");
        var helperStart = client.IndexOf("public async Task AssertUnauthorizedAsync(", StringComparison.Ordinal);
        helperStart.ShouldBeGreaterThan(-1);
        var helper = client[helperStart..client.IndexOf("// --- end WP-V2b unauthenticated ---", helperStart, StringComparison.Ordinal)];
        helper.ShouldContain("CreateUnauthenticatedClient()");
        helper.ShouldContain("!= 401");
        foreach (var forbidden in new[] { ".Ok()", "RowsShouldBe", "IdsShouldBe", "ParseGraphQl", "WebSqlChannel", "Rows" })
        {
            helper.Contains(forbidden, StringComparison.Ordinal).ShouldBeFalse($"the anonymous helper must only assert 401 ('{forbidden}').");
        }

        // The assertion helper is called from exactly one scenario, once per channel.
        var scenarioFiles = Directory.GetFiles(RobinDir, "RobinScenarios*.cs");
        scenarioFiles.Length.ShouldBeGreaterThan(0);
        var callers = scenarioFiles.Where(f => File.ReadAllText(f).Contains("AssertUnauthorizedAsync(", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
        callers.ShouldBe(["RobinScenarios.Wp2.cs"]);
        var wp2 = Read("RobinScenarios.Wp2.cs");
        var body = MethodBody(wp2, "Robin_Proactive_UnauthenticatedRequestRejected");
        Regex.Count(body, @"AssertUnauthorizedAsync\(").ShouldBe(4, "W, G, R and OData.");
        Regex.Count(wp2, @"AssertUnauthorizedAsync\(").ShouldBe(4, "no call outside the unauthenticated scenario.");
        GapKeysOf("Robin_Proactive_UnauthenticatedRequestRejected").ShouldBeEmpty("SEC-R100-12: an unauthenticated cell is never a known gap.");
    }

    /// <summary>SEC-R100-13: unknown source and unknown table are compared on status, code and body shape; neither is a gap.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public void Robin100_UnknownSourceAndUnknownTableAreIndistinguishable()
    {
        var wp2 = Read("RobinScenarios.Wp2.cs");
        var body = MethodBody(wp2, "Robin_30_FederatedUnknownSourceSameCode");
        body.ShouldContain("SQL_POLICY_DENIED");
        body.ShouldContain("unknownSource.Status.ShouldBe(unknownTable.Status)");
        body.ShouldContain("unknownSource.Code.ShouldBe(unknownTable.Code)");
        body.ShouldContain("Shape(unknownSource.RawBody).ShouldBe(Shape(unknownTable.RawBody))");
        body.ShouldContain("NoExistenceStatement(");
        body.ShouldContain("nope.main.parity_sales");
        body.ShouldContain("second.main.parity_nope");
        GapKeysOf("Robin_30_FederatedUnknownSourceSameCode").ShouldBeEmpty("SEC-R100-13: an existence oracle is never a known gap.");
    }

    /// <summary>SEC-R100-14: one alias without the required filter rejects the whole request; data under the unfiltered alias is a denied-request-succeeded failure.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public void Robin100_ScopedAliasWithoutFilterRejectsWholeRequest()
    {
        var wp2 = Read("RobinScenarios.Wp2.cs");
        var body = MethodBody(wp2, "Robin_163_GraphQlAliasesOnScopedTable");
        wp2.ShouldContain("[InlineData(\"unfiltered_alias\")]");
        wp2.ShouldContain("[InlineData(\"single_unfiltered\")]");
        body.ShouldContain("variant == \"unfiltered_alias\"");
        body.ShouldContain("REQUIRED_FILTER_COLUMN_MISSING");
        Regex.Count(body, "DeniedRequestSucceededException").ShouldBe(2, "rows under the unfiltered alias and a 200 without errors.");
        body.ShouldContain("GraphQlAlias(r.RawBody, \"all\") is not null");

        // The only allowed gap is the partial data of the filtered alias next to the rejection code.
        GapKeysOf("Robin_163_GraphQlAliasesOnScopedTable").Where(k => !k.EndsWith("|GraphQl|unfiltered_alias", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    /// <summary>SEC-R100-17: 1000 IN values are accepted, 1001 are rejected with 400 SQL_LIST_PARAM_TOO_LARGE; a 200 is a failure, not a gap.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public void Robin100_InListOverCapIsRejected()
    {
        var wp2 = Read("RobinScenarios.Wp2.cs");
        var body = MethodBody(wp2, "Robin_128_InListOverCapStableCode");
        body.ShouldContain("Range(1, 1000)");
        body.ShouldContain("Range(1, 1001)");
        body.ShouldContain("SQL_LIST_PARAM_TOO_LARGE");
        body.ShouldContain("Status.ShouldBe(400)");
        body.ShouldContain("DeniedRequestSucceededException");
        GapKeysOf("Robin_128_InListOverCapStableCode").ShouldBeEmpty("SEC-R100-17: an over-cap IN list that succeeds is a failure, never a known gap.");
    }
}
