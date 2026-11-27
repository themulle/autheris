namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autheris.Application.Mcp.Services;
using Shouldly;
using Xunit;

public sealed class McpRowShorteningTests
{
    private const string OriginalNext = "original-next-token";

    private static JsonObject Row(int i, int pad = 40) => new() { ["id"] = i, ["name"] = $"row-{i}-" + new string((char)('a' + (i % 26)), pad + (i % 7) * 5) };

    private static string SqlPayload(int rows, out JsonObject original)
    {
        original = new JsonObject
        {
            ["originalSql"] = "SELECT id, name FROM t ORDER BY id",
            ["columns"] = new JsonArray("id", "name"),
            ["rows"] = new JsonArray(Enumerable.Range(0, rows).Select(i => (JsonNode)Row(i)).ToArray()),
            ["rowCount"] = rows,
            ["elapsedMilliseconds"] = 3,
            ["truncated"] = true,
            ["hasMore"] = true,
            ["cursor"] = OriginalNext,
            ["page"] = new JsonObject { ["number"] = 1, ["offset"] = 0, ["size"] = rows },
            ["next"] = OriginalNext,
        };
        return original.ToJsonString();
    }

    private static string DatasetPayload(int rows) => new JsonObject
    {
        ["data"] = new JsonArray(Enumerable.Range(0, rows).Select(i => (JsonNode)Row(i)).ToArray()),
        ["count"] = rows,
        ["offset"] = 20,
        ["limit"] = rows,
        ["hasMore"] = false,
        ["columns"] = new JsonArray("id", "name"),
    }.ToJsonString();

    private static string SamplePayload(int rows) => new JsonObject
    {
        ["id"] = "orders",
        ["isSynthetic"] = false,
        ["rows"] = new JsonArray(Enumerable.Range(0, rows).Select(i => (JsonNode)Row(i)).ToArray()),
        ["rowCount"] = rows,
    }.ToJsonString();

    [Fact]
    [Trait("Category", "Security")]
    public void Shorten_KeepsLongestWholeRowPrefix_EveryRowEqualsOriginal()
    {
        var json = SqlPayload(60, out var original);
        var maxChars = json.Length / 3;

        var outcome = McpRowShortening.Shorten("query_sql", json, maxChars, k => "tok" + k);

        outcome.Status.ShouldBe(McpRowShorteningStatus.Shortened);
        outcome.Json.Length.ShouldBeLessThanOrEqualTo(maxChars);
        var result = JsonNode.Parse(outcome.Json)!.AsObject();
        var rows = result["rows"]!.AsArray();
        rows.Count.ShouldBe(outcome.ReturnedRows);
        outcome.ReturnedRows.ShouldBeInRange(1, 59);
        for (var i = 0; i < rows.Count; i++)
        {
            JsonNode.DeepEquals(rows[i], original["rows"]![i]).ShouldBeTrue();
        }

        // One more whole row would not fit.
        var longer = JsonNode.Parse(outcome.Json)!.AsObject();
        var longerRows = longer["rows"]!.AsArray();
        longerRows.Add(JsonNode.Parse(original["rows"]![rows.Count]!.ToJsonString()));
        longer["rowCount"] = rows.Count + 1;
        longer["next"] = "tok" + (rows.Count + 1);
        longer["cursor"] = "tok" + (rows.Count + 1);
        longer.ToJsonString().Length.ShouldBeGreaterThan(maxChars);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Shorten_ReturnedRows_NeverCutInsideRow()
    {
        var json = DatasetPayload(40);

        for (var maxChars = json.Length / 6; maxChars < json.Length; maxChars += 97)
        {
            var outcome = McpRowShortening.Shorten("query_dataset", json, maxChars, null);
            if (outcome.Status != McpRowShorteningStatus.Shortened)
            {
                continue;
            }

            var parsed = JsonNode.Parse(outcome.Json)!.AsObject();
            parsed["data"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).ShouldAllBe(n => n.StartsWith("row-", StringComparison.Ordinal));
            parsed["data"]!.AsArray().Count.ShouldBe(outcome.ReturnedRows);
            outcome.Json.Length.ShouldBeLessThanOrEqualTo(maxChars);
        }
    }

    [Fact]
    public void Shorten_QuerySql_SetsRowCountHasMoreAndMintedCursor()
    {
        var calls = new List<int>();
        var json = SqlPayload(50, out _);

        var outcome = McpRowShortening.Shorten("query_sql", json, json.Length / 2, k => { calls.Add(k); return "tok" + k; });

        var result = JsonNode.Parse(outcome.Json)!.AsObject();
        result["rowCount"]!.GetValue<int>().ShouldBe(outcome.ReturnedRows);
        result["hasMore"]!.GetValue<bool>().ShouldBeTrue();
        result["next"]!.GetValue<string>().ShouldBe("tok" + outcome.ReturnedRows);
        result["cursor"]!.GetValue<string>().ShouldBe("tok" + outcome.ReturnedRows);
        calls.ShouldContain(outcome.ReturnedRows);
        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result["originalSql"]!.GetValue<string>().ShouldBe("SELECT id, name FROM t ORDER BY id");
        result["page"]!["size"]!.GetValue<int>().ShouldBe(50);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Shorten_QuerySql_NeverPassesOriginalNextThrough()
    {
        var json = SqlPayload(50, out _);

        var outcome = McpRowShortening.Shorten("query_sql", json, json.Length / 2, null);

        outcome.Status.ShouldBe(McpRowShorteningStatus.Shortened);
        outcome.Json.ShouldNotContain(OriginalNext);
        var result = JsonNode.Parse(outcome.Json)!.AsObject();
        result.ContainsKey("next").ShouldBeFalse();
        result.ContainsKey("cursor").ShouldBeFalse();
        result["hasMore"]!.GetValue<bool>().ShouldBeTrue();

        var minted = McpRowShortening.Shorten("query_sql", json, json.Length / 2, k => "fresh" + k);
        minted.Json.ShouldNotContain(OriginalNext);
    }

    [Fact]
    public void Shorten_QueryDataset_SetsCountAndHasMore_KeepsOffsetAndLimit()
    {
        var json = DatasetPayload(40);

        var outcome = McpRowShortening.Shorten("query_dataset", json, json.Length / 2, _ => "must-not-be-used");

        var result = JsonNode.Parse(outcome.Json)!.AsObject();
        result["count"]!.GetValue<int>().ShouldBe(outcome.ReturnedRows);
        result["hasMore"]!.GetValue<bool>().ShouldBeTrue();
        result["offset"]!.GetValue<int>().ShouldBe(20);
        result["limit"]!.GetValue<int>().ShouldBe(40);
        result.ContainsKey("next").ShouldBeFalse();
        result.ContainsKey("cursor").ShouldBeFalse();
    }

    [Fact]
    public void Shorten_SampleRows_SetsRowCount_NoContinuation()
    {
        var json = SamplePayload(40);

        var outcome = McpRowShortening.Shorten("sample_rows", json, json.Length / 2, _ => "must-not-be-used");

        var result = JsonNode.Parse(outcome.Json)!.AsObject();
        result["rowCount"]!.GetValue<int>().ShouldBe(outcome.ReturnedRows);
        result.ContainsKey("hasMore").ShouldBeFalse();
        result.ContainsKey("next").ShouldBeFalse();
        result.ContainsKey("cursor").ShouldBeFalse();
        result["id"]!.GetValue<string>().ShouldBe("orders");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Shorten_FirstRowTooLarge_ReturnsStatusWithoutRows()
    {
        var json = SamplePayload(1);

        var outcome = McpRowShortening.Shorten("sample_rows", json, json.Length / 2, null);

        outcome.Status.ShouldBe(McpRowShorteningStatus.FirstRowTooLarge);
        outcome.ReturnedRows.ShouldBe(0);
        outcome.Json.ShouldNotContain("row-");

        var many = SamplePayload(5);
        McpRowShortening.Shorten("sample_rows", many, 40, null).Status.ShouldBe(McpRowShorteningStatus.FirstRowTooLarge);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Shorten_MaskedAndWithheldValues_UnchangedInReturnedRows()
    {
        var rows = new JsonArray(Enumerable.Range(0, 30).Select(i => (JsonNode)new JsonObject
        {
            ["id"] = i,
            ["email"] = "***",
            ["note"] = "[withheld: suspected prompt injection] äö <b>" + new string('z', 30),
        }).ToArray());
        var json = new JsonObject { ["rows"] = rows, ["rowCount"] = 30 }.ToJsonString();

        var outcome = McpRowShortening.Shorten("sample_rows", json, json.Length / 2, null);

        var returned = JsonNode.Parse(outcome.Json)!["rows"]!.AsArray();
        returned.Count.ShouldBeGreaterThan(0);
        returned.ShouldAllBe(r => r!["email"]!.GetValue<string>() == "***" && r["note"]!.GetValue<string>().StartsWith("[withheld: suspected prompt injection] äö <b>", StringComparison.Ordinal));
    }

    [Fact]
    public void Shorten_SameInput_IsDeterministic()
    {
        var json = SqlPayload(70, out _);

        var first = McpRowShortening.Shorten("query_sql", json, json.Length / 4, k => "tok" + k);
        var second = McpRowShortening.Shorten("query_sql", json, json.Length / 4, k => "tok" + k);

        second.ShouldBe(first);
    }

    [Fact]
    public void Shorten_TenThousandRows_SerializesEachRowOnce()
    {
        var calls = new List<int>();
        var json = SqlPayload(10_000, out _);

        var outcome = McpRowShortening.Shorten("query_sql", json, 4096 * 4, k => { calls.Add(k); return "tok" + k; });

        outcome.Status.ShouldBe(McpRowShorteningStatus.Shortened);
        outcome.ReturnedRows.ShouldBeInRange(1, 9_999);
        calls.Count.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Shorten_ResumeInvokedOnlyWithKBelowRowCount()
    {
        var calls = new List<int>();
        var json = SqlPayload(25, out _);

        McpRowShortening.Shorten("query_sql", json, json.Length - 10, k => { calls.Add(k); return "tok" + k; });

        calls.ShouldNotBeEmpty();
        calls.ShouldAllBe(k => k >= 1 && k < 25);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Shorten_ResumeCalledAtMostTwice()
    {
        var calls = new List<int>();
        var json = SqlPayload(80, out _);

        // A token whose length grows with k forces the second mint.
        McpRowShortening.Shorten("query_sql", json, json.Length / 3, k => { calls.Add(k); return new string('t', 20 + k); });

        calls.Count.ShouldBeLessThanOrEqualTo(2);
    }

    [Theory]
    [InlineData("query_graphql")]
    [InlineData("call_procedure")]
    [InlineData("list_datasets")]
    [InlineData("query_customers")]
    public void Shorten_OtherToolsOrNoRowArray_NotApplicable(string tool)
    {
        McpRowShortening.AppliesTo(tool).ShouldBeFalse();
        McpRowShortening.Shorten(tool, SqlPayload(20, out _), 100, _ => "t").Status.ShouldBe(McpRowShorteningStatus.NotApplicable);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"rows":[],"rowCount":0}""")]
    [InlineData("""{"other":[1,2,3]}""")]
    public void Shorten_UnparseableOrNoRows_NotApplicable(string json)
    {
        McpRowShortening.Shorten("query_sql", json, 5, _ => "t").Status.ShouldBe(McpRowShorteningStatus.NotApplicable);
    }

    [Fact]
    public void Shorten_ReserializedFullResultFits_ReturnsFits()
    {
        var spaced = "{ \"rows\" : [ { \"id\" : 1 } , { \"id\" : 2 } ] , \"rowCount\" : 2 }";
        var compactLength = JsonNode.Parse(spaced)!.ToJsonString().Length;

        var outcome = McpRowShortening.Shorten("sample_rows", spaced, compactLength, null);

        outcome.Status.ShouldBe(McpRowShorteningStatus.Fits);
        outcome.Json.Length.ShouldBe(compactLength);
    }

    [Fact]
    public void FirstRowNote_IsFixedTextPerTool()
    {
        foreach (var tool in new[] { "query_sql", "query_dataset", "sample_rows" })
        {
            var note = McpRowShortening.FirstRowNote(tool);
            note.Length.ShouldBeLessThan(512);
            note.ShouldContain("single row exceeds the token budget");
        }

        McpRowShortening.FirstRowNote("query_dataset").ShouldContain("select");
    }
}
