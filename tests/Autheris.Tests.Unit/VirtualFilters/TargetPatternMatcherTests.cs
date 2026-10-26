namespace Autheris.Tests.Unit.VirtualFilters;

using Autheris.Tests.Common;
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 WI-2 (SEC-7, SEC-8): regex target patterns of Virtual Filter bindings. They match the full,
/// source-qualified name <c>domain.schema.table</c> in linear time; invalid, unsupported or overlong patterns are
/// rejected on write, and at match time every error propagates (the resolver denies).
/// </summary>
public sealed class TargetPatternMatcherTests
{
    private static readonly TableIdentifier Customers = new("crm_api", "public", "customers");

    [Theory]
    [InlineData("regex:^crm_api\\.public\\.customers$", true)]
    [InlineData("^crm_api\\.public\\.customers$", true)]
    [InlineData("regex:crm_api\\.public\\.customers", true)]     // anchoring is implicit (full match)
    [InlineData("regex:^CRM_API\\.Public\\.Customers$", true)]   // case-insensitive like TableIdentifier
    [InlineData("regex:^public\\.customers$", false)]            // the source is part of the matched name
    [InlineData("regex:^crm_api\\.public\\.cust", false)]        // a prefix is not a full match
    [InlineData("^erp\\.public\\.customers$", false)]
    public void IsMatch_RegexIsFullMatchOnSourceQualifiedName(string pattern, bool expected)
    {
        // ST-6
        TargetPatternMatcher.IsMatch(pattern, Customers).ShouldBe(expected);
        new FilterBinding { FilterName = "filter_a", TargetPattern = pattern }.MatchesTarget(Customers).ShouldBe(expected);
    }

    [Theory]
    [InlineData("^(a)\\1$")]       // backreference
    [InlineData("^(?=x).*")]       // lookahead
    [InlineData("regex:(?<=a)b")]  // lookbehind
    [InlineData("regex:^crm_api\\.(public")]
    [InlineData("regex:x)|(?:.*")]  // would close the anchoring group and match everything
    public void Validate_RejectsInvalidAndBacktrackingOnlyPatterns(string pattern)
    {
        // ST-8
        Should.Throw<ArgumentException>(() => new FilterBinding { FilterName = "filter_a", TargetPattern = pattern }.Validate());
    }

    [Fact]
    public void Validate_RejectsPatternsLongerThan256Characters()
    {
        // ST-8
        TargetPatternMatcher.MaxPatternLength.ShouldBe(256);
        var overlong = "^" + new string('a', 256);   // 257 characters
        var longest = "^" + new string('a', 255);    // 256 characters

        Should.Throw<ArgumentException>(() => new FilterBinding { FilterName = "filter_a", TargetPattern = overlong }.Validate());
        Should.Throw<ArgumentException>(() => new FilterBinding { FilterName = "filter_a", TargetPattern = "regex:" + overlong }.Validate());
        Should.NotThrow(() => new FilterBinding { FilterName = "filter_a", TargetPattern = longest }.Validate());
    }

    [Fact]
    public void Validate_AcceptsALinearSourceQualifiedRegex()
    {
        Should.NotThrow(() => new FilterBinding { FilterName = "filter_a", TargetPattern = "regex:^crm_api\\.public\\.(customers|contacts)$" }.Validate());
    }

    [Fact]
    public void IsMatch_InvalidStoredPattern_Throws()
    {
        // A pattern written to the repository without Validate: the error propagates, it is never "no match".
        Should.Throw<ArgumentException>(() => TargetPatternMatcher.IsMatch("regex:^crm_api\\.(public", Customers));
        Should.Throw<ArgumentException>(() => new FilterBinding { FilterName = "filter_a", TargetPattern = "regex:^crm_api\\.(public" }.MatchesTarget(Customers));
        Should.Throw<NotSupportedException>(() => TargetPatternMatcher.IsMatch("^(a)\\1$", Customers));
        Should.Throw<ArgumentException>(() => TargetPatternMatcher.IsMatch("^" + new string('a', 300), Customers));
    }

    [Fact]
    public void IsMatch_Timeout_Propagates()
    {
        // Proves the matcher has no catch: a (backtracking) regex that times out surfaces the timeout.
        var slow = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var pathological = new TableIdentifier(new string('a', 5_000) + "!", "s", "t");

        Should.Throw<RegexMatchTimeoutException>(() => TargetPatternMatcher.IsMatch(slow, pathological));
    }

    [Fact]
    public async Task IsMatch_UsesTheLinearEngine_PathologicalInputReturnsFalseQuickly()
    {
        // ST-9
        var pathological = new TableIdentifier(new string('a', 50_000) + "!", "s", "t");
        TargetPatternMatcher.IsMatch("^(a+)+$", Customers).ShouldBeFalse();   // warm-up (construction and cache)

        // A backtracking engine needs exponential time here and never returns; the hang guard turns that into a failure.
        var matched = await WaitHelper.RunGuarded(() => TargetPatternMatcher.IsMatch("^(a+)+$", pathological));

        matched.ShouldBeFalse();
    }

    [Theory]
    [InlineData("^x", true)]
    [InlineData("regex:x", true)]
    [InlineData("REGEX:x", true)]
    [InlineData("crm_api.public.customers", false)]
    [InlineData("crm_api.*.(cust.*)", false)]
    [InlineData(null, false)]
    public void IsRegex_RecognisesTheRegexForms(string? pattern, bool expected) =>
        TargetPatternMatcher.IsRegex(pattern).ShouldBe(expected);
}
