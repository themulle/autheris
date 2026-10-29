using System.Globalization;
using System.Text.Json;

namespace SqlGate.BenchCheck;

/// <summary>Reads the BenchmarkDotNet JSON export (<c>--exporters json</c>) and the frozen baseline file.</summary>
public static class BenchResultReader
{
    private const int BaselineSchema = 1;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static IReadOnlyList<BenchSample> ReadSamples(string json)
    {
        using var doc = Parse(json);
        if (!doc.RootElement.TryGetProperty("Benchmarks", out var benchmarks) || benchmarks.ValueKind != JsonValueKind.Array || benchmarks.GetArrayLength() == 0)
        {
            throw new BenchCheckInputException("The benchmark report has no 'Benchmarks' entries.");
        }

        var samples = new List<BenchSample>();
        foreach (var benchmark in benchmarks.EnumerateArray())
        {
            string method = Text(benchmark, "Method");
            if (!Enum.TryParse(method, false, out BenchEngine engine) || !Enum.IsDefined(engine))
            {
                continue;   // AstCached and friends are informational, not gated
            }

            string bucket = BucketOf(Text(benchmark, "Parameters"));
            var statistics = Member(benchmark, "Statistics");
            var memory = Member(benchmark, "Memory");
            var values = Member(statistics, "OriginalValues").EnumerateArray().Select(v => v.GetDouble()).Order().ToArray();
            if (values.Length == 0)
            {
                throw new BenchCheckInputException($"{method}/{bucket} has no measurements.");
            }

            double operations = Member(memory, "TotalOperations").GetDouble();
            if (operations <= 0)
            {
                throw new BenchCheckInputException($"{method}/{bucket} reports no operations.");
            }

            samples.Add(new BenchSample(
                bucket,
                engine,
                values.Average(),
                Percentile(values, 0.95),
                Percentile(values, 0.99),
                Member(memory, "BytesAllocatedPerOperation").GetDouble(),
                Member(memory, "Gen2Collections").GetDouble() * 1000.0 / operations));
        }

        return samples;
    }

    public static BenchBaseline ReadBaseline(string json)
    {
        using var doc = Parse(json);
        if (!doc.RootElement.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != BaselineSchema)
        {
            throw new BenchCheckInputException($"The baseline must declare \"schema\": {BaselineSchema}.");
        }

        var buckets = new Dictionary<string, BaselineBucket>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("buckets", out var element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in element.EnumerateObject())
            {
                buckets[bucket.Name] = new BaselineBucket(
                    Member(bucket.Value, "legacyAllocatedBytesPerOp").GetDouble(),
                    Member(bucket.Value, "astAllocatedBytesPerOp").GetDouble(),
                    bucket.Value.TryGetProperty("legacyGen2PerThousandOps", out var gen2) && gen2.ValueKind == JsonValueKind.Number ? gen2.GetDouble() : null);
            }
        }

        if (buckets.Count == 0)
        {
            throw new BenchCheckInputException("The baseline has no buckets.");
        }

        return new BenchBaseline(buckets);
    }

    /// <summary>Renders the baseline file. Only allocation is frozen: it does not depend on the hardware, unlike time.</summary>
    public static string WriteBaseline(IReadOnlyList<BenchSample> samples, string machine, string capturedOn)
    {
        var buckets = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var group in samples.GroupBy(s => s.Bucket))
        {
            var legacy = group.FirstOrDefault(s => s.Engine == BenchEngine.Legacy);
            var ast = group.FirstOrDefault(s => s.Engine == BenchEngine.Ast);
            if (legacy is null || ast is null)
            {
                throw new BenchCheckInputException($"Bucket {group.Key} needs a legacy and an AST sample to freeze a baseline.");
            }

            buckets[group.Key] = new
            {
                legacyAllocatedBytesPerOp = Math.Round(legacy.AllocatedBytesPerOp),
                astAllocatedBytesPerOp = Math.Round(ast.AllocatedBytesPerOp),
                legacyGen2PerThousandOps = Math.Round(legacy.Gen2CollectionsPerThousandOps, 1)
            };
        }

        var document = new
        {
            schema = BaselineSchema,
            description = "G8 allocation baseline of the SQL compiler (plan 9.3). Time is never frozen: it is compared by ratio on the same runner.",
            capturedOn,
            machine,
            buckets
        };
        return JsonSerializer.Serialize(document, Indented) + "\n";
    }

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BenchCheckInputException("The input is not valid JSON.", ex);
        }
    }

    internal static JsonElement Member(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            throw new BenchCheckInputException($"The input has no '{name}' member where one is required.");
        }

        return value;
    }

    private static string Text(JsonElement element, string name) =>
        Member(element, name).GetString() ?? throw new BenchCheckInputException($"'{name}' is null.");

    private static string BucketOf(string parameters)
    {
        const string prefix = "Bucket=";
        int start = parameters.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new BenchCheckInputException($"The benchmark parameters '{parameters}' contain no bucket.");
        }

        string rest = parameters[(start + prefix.Length)..];
        int end = rest.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>Linear interpolation between the closest ranks of the sorted measurements.</summary>
    internal static double Percentile(double[] sorted, double p)
    {
        double rank = p * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + ((rank - lower) * (sorted[upper] - sorted[lower]));
    }
}
