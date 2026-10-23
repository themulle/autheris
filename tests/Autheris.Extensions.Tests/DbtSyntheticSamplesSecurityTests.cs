namespace Autheris.Extensions.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Extensions.Dbt;
using Shouldly;
using Xunit;

/// <summary>MCP-02: dbt <c>meta</c> synthetic samples are inline JSON only; no string is ever read as a file path.</summary>
[Trait("Category", "Security")]
public sealed class DbtSyntheticSamplesSecurityTests
{
    private static readonly string[] Columns = ["id", "name"];

    private static JsonElement Meta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ExtractSyntheticSamples_StringPath_IgnoredAndNoFileRead()
    {
        // MCP-02: absolute path, ../ path and a real temp file must all yield null.
        var tempFile = Path.Combine(Path.GetTempPath(), $"mcp02_{Guid.NewGuid():N}.json");
        File.WriteAllText(tempFile, "[{\"id\": 1, \"name\": \"leaked\"}]");
        try
        {
            var absolute = JsonSerializer.Serialize(tempFile);
            DbtArtifactStreamingParser.ExtractSyntheticSamples(Meta($$"""{"synthetic_samples": {{absolute}}}"""), Columns).ShouldBeNull();
            DbtArtifactStreamingParser.ExtractSyntheticSamples(Meta("""{"synthetic_samples": "../../etc/passwd.csv"}"""), Columns).ShouldBeNull();
            DbtArtifactStreamingParser.ExtractSyntheticSamples(Meta("""{"synthetic_samples": "samples.json"}"""), Columns).ShouldBeNull();
            // Even an inline JSON array carried in a string is not interpreted any more.
            DbtArtifactStreamingParser.ExtractSyntheticSamples(Meta("""{"synthetic_samples": "[{\"id\":1}]"}"""), Columns).ShouldBeNull();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExtractSyntheticSamples_SamplesAliasIgnored()
    {
        DbtArtifactStreamingParser.ExtractSyntheticSamples(Meta("""{"samples": [{"id": 1}]}"""), Columns).ShouldBeNull();
    }

    [Fact]
    public void ExtractSyntheticSamples_MoreThan100Rows_Truncated()
    {
        var rows = string.Join(",", Enumerable.Range(1, 150).Select(i => $"{{\"id\": {i}}}"));
        var result = DbtArtifactStreamingParser.ExtractSyntheticSamples(Meta($$"""{"synthetic_samples": [{{rows}}]}"""), Columns);

        result.ShouldNotBeNull();
        result!.Count.ShouldBe(100);
    }

    [Fact]
    public void ExtractSyntheticSamples_UnknownKeys_Dropped()
    {
        var result = DbtArtifactStreamingParser.ExtractSyntheticSamples(
            Meta("""{"synthetic_samples": [{"id": 1, "password": "x", "NAME": "n"}]}"""), Columns);

        result.ShouldNotBeNull();
        result![0].ContainsKey("password").ShouldBeFalse();
        result[0]["id"].ShouldBe(1L);
        result[0]["NAME"].ShouldBe("n");
    }

    [Fact]
    public void ExtractSyntheticSamples_NestedObjectValue_Dropped()
    {
        var result = DbtArtifactStreamingParser.ExtractSyntheticSamples(
            Meta("""{"synthetic_samples": [{"id": 1, "name": {"a": "b"}}]}"""), Columns);

        result.ShouldNotBeNull();
        result![0].ContainsKey("name").ShouldBeFalse();
        result[0]["id"].ShouldBe(1L);
    }

    [Fact]
    public void ExtractSyntheticSamples_OversizedString_Skipped()
    {
        var huge = new string('a', 1025);
        var result = DbtArtifactStreamingParser.ExtractSyntheticSamples(
            Meta($$"""{"synthetic_samples": [{"id": 1, "name": "{{huge}}"}]}"""), Columns);

        result.ShouldNotBeNull();
        result![0].ContainsKey("name").ShouldBeFalse();
    }

    [Fact]
    public async Task ParseManifest_StringPathSample_YieldsNoSamples()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mcp02_{Guid.NewGuid():N}.json");
        File.WriteAllText(tempFile, "[{\"id\": 42}]");
        try
        {
            var manifest = $$"""
            {
              "nodes": {
                "model.a.orders": {
                  "name": "orders", "database": "db", "schema": "a",
                  "config": { "materialized": "table" },
                  "meta": { "synthetic_samples": {{JsonSerializer.Serialize(tempFile)}} },
                  "columns": { "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] } },
                  "depends_on": { "nodes": [] }
                }
              }
            }
            """;

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
            var models = await DbtArtifactStreamingParser.ParseManifestStreamAsync(stream);

            models.Single(m => m.Name == "orders").SyntheticSamples.ShouldBeNull();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void DbtArtifactStreamingParser_DoesNotReferenceSystemIoFile()
    {
        // MCP-02 condition 4: the parser has no System.IO.File / Directory / Path dependency.
        var type = typeof(DbtArtifactStreamingParser);
        var source = FindSource("DbtArtifactStreamingParser.cs");
        source.ShouldNotContain("File.");
        source.ShouldNotContain("Directory.");
        source.ShouldNotContain("Path.");
        type.ShouldNotBeNull();
    }

    private static string FindSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Autheris.Extensions", "Dbt", fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
