namespace Autheris.Tests.SqlCompilerGate;

using System.Globalization;
using Shouldly;
using SqlGate.BenchCheck;

/// <summary>
/// ARCH-MNT-15-FU-01 (WAVE3-SQLGATE-SC-07): the cold-cache G8 gate. The cold/warm P99 ratio of the ANTLR parser cache benchmark
/// is limited; a missing report, a missing method or unusable samples fail closed as <c>missing-data</c>.
/// </summary>
public sealed class ParserCacheGateTests
{
    private static string Entry(string method, double[] values) =>
        string.Create(CultureInfo.InvariantCulture, $$$"""
        {"Method":"{{{method}}}","Parameters":"","Statistics":{"OriginalValues":[{{{string.Join(',', values.Select(v => v.ToString(CultureInfo.InvariantCulture)))}}}]}}
        """);

    private static string Report(params string[] entries) => "{\"Benchmarks\":[" + string.Join(',', entries) + "]}";

    private static string Healthy(double coldScale = 2) => Report(
        Entry("ParseWarm", [100, 200, 300, 400, 500]),
        Entry("ParseCold", [.. new double[] { 100, 200, 300, 400, 500 }.Select(v => v * coldScale)]));

    [Fact]
    public void ParserCache_ReadsWarmAndColdP99()
    {
        var sample = ParserCacheResultReader.ReadSamples(Healthy(2));

        sample.WarmP99Ns.ShouldBe(496, 1e-9);
        sample.ColdP99Ns.ShouldBe(992, 1e-9);
    }

    [Fact]
    public void ParserCache_RatioAboveLimit_Violation()
    {
        var thresholds = new BenchThresholds { MaxColdWarmP99Ratio = 3.0 };

        ParserCacheGate.Check([Healthy(3.5)], thresholds).Violations.ShouldContain(v => v.Rule == "cold-warm-ratio-p99");
        ParserCacheGate.Check([Healthy(2.9)], thresholds).Passed.ShouldBeTrue();
        ParserCacheGate.Check([Healthy(3.0)], thresholds).Passed.ShouldBeTrue();   // exactly at the limit
    }

    [Fact]
    public void ParserCache_ReportMissing_MissingData()
    {
        var verdict = ParserCacheGate.Check([], new BenchThresholds());

        verdict.Passed.ShouldBeFalse();
        verdict.Violations.ShouldContain(v => v.Rule == "missing-data");
    }

    [Theory]
    [InlineData("ParseWarm")]
    [InlineData("ParseCold")]
    public void ParserCache_MethodMissing_MissingData(string present)
    {
        var verdict = ParserCacheGate.Check([Report(Entry(present, [1, 2, 3]))], new BenchThresholds());

        verdict.Violations.ShouldContain(v => v.Rule == "missing-data");
    }

    [Fact]
    public void ParserCache_EmptySamples_MissingData()
    {
        string json = Report(Entry("ParseWarm", []), Entry("ParseCold", [1, 2, 3]));

        ParserCacheGate.Check([json], new BenchThresholds()).Violations.ShouldContain(v => v.Rule == "missing-data");
        Should.Throw<BenchCheckInputException>(() => ParserCacheResultReader.ReadSamples(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Benchmarks\":[]}")]
    public void ParserCache_UnreadableReport_MissingData(string json)
    {
        ParserCacheGate.Check([json], new BenchThresholds()).Violations.ShouldContain(v => v.Rule == "missing-data");
    }

    [Fact]
    public void ParserCache_NonPositiveWarmP99_MissingData()
    {
        string json = Report(Entry("ParseWarm", [0, 0, 0]), Entry("ParseCold", [1, 2, 3]));

        ParserCacheGate.Check([json], new BenchThresholds()).Violations.ShouldContain(v => v.Rule == "missing-data");
    }
}
