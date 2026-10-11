namespace Autheris.Tests.SqlCompilerGate;

using System.Globalization;
using Shouldly;
using SqlGate.BenchCheck;

/// <summary>Reads the BenchmarkDotNet JSON export (the shape of <c>--exporters json</c>) and the frozen baseline file.</summary>
public sealed class BenchCheckReaderTests
{
    private static string Entry(string method, string bucket, double[] values, long gen2, long ops, long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $$$"""
        {"Method":"{{{method}}}","Parameters":"Bucket={{{bucket}}}","Statistics":{"OriginalValues":[{{{string.Join(',', values.Select(v => v.ToString(CultureInfo.InvariantCulture)))}}}],"Mean":{{{values.Average().ToString(CultureInfo.InvariantCulture)}}}},
         "Memory":{"Gen0Collections":3,"Gen1Collections":2,"Gen2Collections":{{{gen2}}},"TotalOperations":{{{ops}}},"BytesAllocatedPerOperation":{{{bytes}}}}}
        """);

    private static string Report(params string[] entries) => "{\"Title\":\"t\",\"Benchmarks\":[" + string.Join(',', entries) + "]}";

    [Fact]
    public void BenchCheck_ResultReader_ReadsLegacyAndAst_IgnoresOtherMethods()
    {
        string json = Report(
            Entry("Legacy", "4KB", [100, 200, 300], 0, 1000, 5000),
            Entry("Ast", "4KB", [110, 210, 310], 2, 1000, 6000),
            Entry("AstCached", "4KB", [1, 2, 3], 0, 1000, 10));

        var samples = BenchResultReader.ReadSamples(json);

        samples.Count.ShouldBe(2);
        var ast = samples.Single(s => s.Engine == BenchEngine.Ast);
        ast.Bucket.ShouldBe("4KB");
        ast.AllocatedBytesPerOp.ShouldBe(6000);
        ast.Gen2CollectionsPerThousandOps.ShouldBe(2.0);   // 2 collections over 1000 operations
        ast.MeanNs.ShouldBe(210);
    }

    [Fact]
    public void BenchCheck_ResultReader_PercentilesAreInterpolatedFromTheMeasurements()
    {
        var sample = BenchResultReader.ReadSamples(Report(Entry("Legacy", "0.5KB", [100, 200, 300, 400, 500], 0, 10, 1))).Single();
        sample.P95Ns.ShouldBe(480, 1e-9);   // rank 0.95 * 4 = 3.8 -> 400 + 0.8 * 100 = 480
        sample.P99Ns.ShouldBe(496, 1e-9);   // rank 0.99 * 4 = 3.96 -> 400 + 0.96 * 100 = 496
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Benchmarks\":[]}")]
    [InlineData("{\"Benchmarks\":[{\"Method\":\"Ast\",\"Parameters\":\"Bucket=4KB\"}]}")]
    public void BenchCheck_ResultReader_MalformedOrIncompleteInput_Throws(string json)
    {
        Should.Throw<BenchCheckInputException>(() => BenchResultReader.ReadSamples(json));
    }

    [Fact]
    public void BenchCheck_Baseline_RoundTripsAllocationNumbers()
    {
        var samples = new List<BenchSample>
        {
            new("4KB", BenchEngine.Legacy, 1, 1, 1, 1000, 0),
            new("4KB", BenchEngine.Ast, 1, 1, 1, 1100, 0)
        };

        string json = BenchResultReader.WriteBaseline(samples, "test-machine", "2026-10-11");
        var baseline = BenchResultReader.ReadBaseline(json);

        baseline.Buckets["4KB"].LegacyAllocatedBytesPerOp.ShouldBe(1000);
        baseline.Buckets["4KB"].AstAllocatedBytesPerOp.ShouldBe(1100);
        json.ShouldContain("test-machine");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"schema\":1,\"buckets\":{}}")]
    [InlineData("{\"schema\":2,\"buckets\":{\"4KB\":{\"legacyAllocatedBytesPerOp\":1,\"astAllocatedBytesPerOp\":1}}}")]
    public void BenchCheck_Baseline_EmptyOrUnknownSchema_Throws(string json)
    {
        Should.Throw<BenchCheckInputException>(() => BenchResultReader.ReadBaseline(json));
    }
}
