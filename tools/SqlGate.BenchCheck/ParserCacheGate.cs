using System.Text.Json;

namespace SqlGate.BenchCheck;

/// <summary>P99 of the warm and the cold ANTLR cache parse (nanoseconds per parse).</summary>
public sealed record ParserCacheSample(double WarmP99Ns, double ColdP99Ns);

/// <summary>Reads the <c>SqlParserCacheBenchmarks</c> BenchmarkDotNet export (ARCH-MNT-15-FU-01).</summary>
public static class ParserCacheResultReader
{
    private const string WarmMethod = "ParseWarm";
    private const string ColdMethod = "ParseCold";

    /// <exception cref="BenchCheckInputException">A method is missing, has no or non-finite/non-positive measurements.</exception>
    public static ParserCacheSample ReadSamples(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BenchCheckInputException("The parser cache report is not valid JSON.", ex);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("Benchmarks", out var benchmarks) || benchmarks.ValueKind != JsonValueKind.Array || benchmarks.GetArrayLength() == 0)
            {
                throw new BenchCheckInputException("The parser cache report has no 'Benchmarks' entries.");
            }

            return new ParserCacheSample(P99(benchmarks, WarmMethod), P99(benchmarks, ColdMethod));
        }
    }

    private static double P99(JsonElement benchmarks, string method)
    {
        foreach (var benchmark in benchmarks.EnumerateArray())
        {
            if (!benchmark.TryGetProperty("Method", out var name) || name.GetString() != method)
            {
                continue;
            }

            var raw = BenchResultReader.Member(BenchResultReader.Member(benchmark, "Statistics"), "OriginalValues");
            var values = raw.EnumerateArray().Select(v => v.GetDouble()).Order().ToArray();
            if (values.Length == 0 || values.Any(v => !double.IsFinite(v)))
            {
                throw new BenchCheckInputException($"{method} has no usable measurements.");
            }

            double p99 = BenchResultReader.Percentile(values, 0.99);
            if (!double.IsFinite(p99) || p99 <= 0)
            {
                throw new BenchCheckInputException($"{method} has a non-positive P99.");
            }

            return p99;
        }

        throw new BenchCheckInputException($"The parser cache report has no '{method}' benchmark.");
    }
}

/// <summary>Evaluates the cold/warm parser cache ratio; every unreadable or missing input is a <c>missing-data</c> violation (fail closed).</summary>
public static class ParserCacheGate
{
    private const string Subject = "parser-cache";

    public static BenchVerdict Check(IReadOnlyList<string> reportJsons, BenchThresholds thresholds)
    {
        if (reportJsons.Count == 0)
        {
            return Missing("No SqlParserCacheBenchmarks JSON report was found.");
        }

        ParserCacheSample sample;
        try
        {
            // Several exports are possible; the last one in the caller's order (oldest first) wins.
            sample = ParserCacheResultReader.ReadSamples(reportJsons[^1]);
        }
        catch (BenchCheckInputException ex)
        {
            return Missing(ex.Message);
        }

        double ratio = sample.ColdP99Ns / sample.WarmP99Ns;
        if (!double.IsFinite(ratio) || ratio > thresholds.MaxColdWarmP99Ratio)
        {
            return new BenchVerdict([new BenchViolation("cold-warm-ratio-p99", Subject,
                $"Cold/warm P99 is {ratio:F2}x, limit {thresholds.MaxColdWarmP99Ratio:F2}x.")]);
        }

        return new BenchVerdict([]);
    }

    private static BenchVerdict Missing(string message) => new([new BenchViolation("missing-data", Subject, message)]);
}
