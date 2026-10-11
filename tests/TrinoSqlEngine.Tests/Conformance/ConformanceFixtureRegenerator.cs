using System.Text.Json;
using Xunit;

namespace TrinoSqlEngine.Tests.Conformance;

/// <summary>
/// Rewrites the golden fixture files from the current generator output. It only runs with <c>CONFORMANCE_UPDATE=1</c>; the diff of the
/// JSON files is the review artifact for an intentional change of emitted SQL. Semantic notes come from <c>semantic-notes.json</c>.
/// </summary>
public class ConformanceFixtureRegenerator
{
    [Fact]
    public void Regenerate_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("CONFORMANCE_UPDATE") != "1") return;

        var dir = ConformanceStore.SourceDirectory;
        var corpus = ConformanceStore.LoadCorpus(dir);
        var notes = ConformanceStore.LoadNotes(dir);

        foreach (var (name, dialect) in ConformanceDialects.All)
        {
            if (ConformanceVerifier.IsPending(dialect)) continue;   // keep the golden file generated where the capability entry exists
            var cases = new List<ConformanceFixture>();
            foreach (var input in corpus)
            {
                var o = ConformanceRunner.Run(input.Sql, input.Group, dialect);
                string? note = null;
                if (notes.TryGetValue(input.Id, out var byDialect))
                {
                    note = byDialect.GetValueOrDefault(name) ?? byDialect.GetValueOrDefault("*");
                }
                cases.Add(new ConformanceFixture(input.Id, input.Group, input.Sql, o.Outcome, o.Sql, o.Parameters, o.ExceptionType, note));
            }
            File.WriteAllText(Path.Combine(dir, name + ".json"),
                JsonSerializer.Serialize(new ConformanceFile(name, cases), ConformanceStore.Json) + "\n");
        }
    }
}
