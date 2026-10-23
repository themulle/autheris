using System.Globalization;
using System.Net.Http.Json;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Invariant;
using Shouldly;
using TrinoSqlEngine;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Spec;

/// <summary>Block F, advertised SQL functions: every function of <c>SqlFunctionAllowlists.Sqlite</c> is called once over the seed (D-12).</summary>
[Trait("Category", "Parity")]
[Collection(ParityHostCollection.Name)]
public sealed class SpecFunctionTests(ParityHostFixture host) : ParityTestBase
{
    public static IEnumerable<object[]> Functions() =>
        SqlFunctionAllowlists.Sqlite.Order(StringComparer.Ordinal).Select(n => new object[] { n });

    public static IEnumerable<object[]> UnsupportedFunctions() =>
        SpecKnownGaps.UnsupportedFunctions.Order(StringComparer.Ordinal).Select(n => new object[] { n });

    [ParityFact]
    public void Spec_EveryAllowlistedFunctionHasSignature()
    {
        var missing = SqlFunctionAllowlists.Sqlite.Where(n => FunctionSignatures.Find(n) is null).Order(StringComparer.Ordinal).ToList();
        missing.ShouldBeEmpty($"functions without a signature row: {string.Join(", ", missing)}");
    }

    [ParityFact]
    public void Spec_SignatureRowsAreAdvertisedAndUnique()
    {
        var names = FunctionSignatures.All.Select(s => s.Name).ToList();
        names.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(names.Count, "duplicate signature rows");
        var stale = names.Where(n => !SqlFunctionAllowlists.Sqlite.Contains(n) && !SpecKnownGaps.UnsupportedFunctions.Contains(n)).ToList();
        stale.ShouldBeEmpty($"signature rows for functions that are no longer advertised: {string.Join(", ", stale)}");
        FunctionSignatures.All.ShouldAllBe(s => s.Select.Contains("parity_sales", StringComparison.Ordinal) && s.Args.Length > 0);
    }

    [ParityFact]
    public void Spec_SpecKnownGapsReferToAdvertisedFunctions()
    {
        foreach (var n in SpecKnownGaps.Functions.Keys)
        {
            SqlFunctionAllowlists.Sqlite.Contains(n).ShouldBeTrue($"'{n}' is classified but not advertised");
        }

        foreach (var n in SpecKnownGaps.UnsupportedFunctions)
        {
            SqlFunctionAllowlists.Sqlite.Contains(n).ShouldBeFalse($"'{n}' is expected-unsupported but still advertised");
            FunctionSignatures.Find(n).ShouldNotBeNull($"'{n}' has no signature row for its unsupported call");
        }

        SpecKnownGaps.Functions.Keys.Intersect(SpecKnownGaps.UnsupportedFunctions).ShouldBeEmpty("a function is both gap and unsupported");
    }

    [ParityTheory]
    [MemberData(nameof(Functions))]
    public Task Spec_Function(string name) =>
        ParityCell.RunAsync(new ParityCellInfo("spec", "WebSql", "function", name, GapId: SpecKnownGaps.ForFunction(name)), () => CallAsync(name));

    /// <summary>Functions SQLite cannot run are not advertised: the call is a fail-closed 4xx with the catalogue code, never a 5xx.</summary>
    [ParityTheory]
    [MemberData(nameof(UnsupportedFunctions))]
    public Task Spec_UnsupportedFunction(string name) =>
        ParityCell.RunAsync(new ParityCellInfo("spec", "WebSql", "function-unsupported", name), async () =>
        {
            var sig = FunctionSignatures.Find(name) ?? throw new XunitException($"no signature row for '{name}'");
            using var client = host.CreateClient();
            using var response = await client.PostAsJsonAsync("/api/v1/sql", new { sql = sig.Sql });
            var body = await response.Content.ReadAsStringAsync();
            var result = WebSqlChannel.Parse((int)response.StatusCode, body);

            ParityAssert.NoServerError(result, sig.Sql);
            result.Status.ShouldBe(SpecKnownGaps.UnsupportedFunctionStatus, Snip(body));
            result.Code.ShouldBe(SpecKnownGaps.UnsupportedFunctionCode, Snip(body));
        });

    private async Task CallAsync(string name)
    {
        var sig = FunctionSignatures.Find(name) ?? throw new XunitException($"no signature row for '{name}'");
        using var client = host.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/sql", new { sql = sig.Sql });
        var body = await response.Content.ReadAsStringAsync();
        var result = WebSqlChannel.Parse((int)response.StatusCode, body);

        ParityAssert.NoServerError(result, sig.Sql);
        // every advertised function must be callable once: any rejection, 4xx included, is a deviation (a gap while tracked)
        if (ParityAssert.IsError(result))
        {
            throw new XunitException($"{name}: advertised function rejected, HTTP {result.Status} '{result.Code}': {Snip(body)}");
        }

        Console.WriteLine($"spec-function: ok {name}{(sig.CheckValue ? " (value checked)" : string.Empty)}");
        result.Status.ShouldBe(200, Snip(body));
        result.Rows.Count.ShouldBeGreaterThan(0, $"{name}: no row: {Snip(body)}");
        var actual = result.Rows[0].Values.FirstOrDefault();
        if (sig.CheckValue)
        {
            Matches(sig.Expected, actual).ShouldBeTrue($"{name}: expected {sig.Expected ?? "NULL"}, got {actual ?? "NULL"}: {Snip(body)}");
        }
    }

    public static bool Matches(object? expected, object? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        if (IsNumber(expected) && IsNumber(actual))
        {
            var e = Convert.ToDouble(expected, CultureInfo.InvariantCulture);
            var a = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
            return Math.Abs(e - a) <= 1e-6 * Math.Max(1, Math.Abs(e));
        }

        return string.Equals(Convert.ToString(expected, CultureInfo.InvariantCulture), Convert.ToString(actual, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool IsNumber(object o) => o is int or long or double or decimal;

    private static string Snip(string body) => body.Length <= 240 ? body : body[..240] + "...";
}
