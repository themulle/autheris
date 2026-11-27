namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Text.Json;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Paging;
using Shouldly;
using Xunit;

public sealed class McpSqlParametersTests
{
    private static bool Read(string argumentsJson, out IReadOnlyDictionary<string, object?>? parameters)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        return McpSqlParameters.TryRead(doc.RootElement, out parameters);
    }

    [Theory]
    [InlineData("""{"query":"SELECT 1"}""")]
    [InlineData("""{"query":"SELECT 1","parameters":null}""")]
    [InlineData("""{"query":"SELECT 1","parameters":{}}""")]
    public void TryRead_AbsentNullOrEmpty_ReturnsNullParameters(string json)
    {
        Read(json, out var parameters).ShouldBeTrue();
        parameters.ShouldBeNull();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("""{"parameters":"x"}""")]
    [InlineData("""{"parameters":[1,2]}""")]
    [InlineData("""{"parameters":5}""")]
    [InlineData("""{"parameters":true}""")]
    public void TryRead_NonObject_IsRejected(string json)
    {
        Read(json, out var parameters).ShouldBeFalse();
        parameters.ShouldBeNull();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("""{"parameters":{"a":1,"a":2}}""")]
    [InlineData("""{"parameters":{"a":1,"A":2}}""")]
    [InlineData("""{"parameters":{"a":1,"@a":2}}""")]
    [InlineData("""{"parameters":{"@A":1,"a":2}}""")]
    public void TryRead_ExactCaseAndAtPrefixDuplicates_AreRejected(string json)
    {
        Read(json, out var parameters).ShouldBeFalse();
        parameters.ShouldBeNull();
    }

    [Fact]
    public void TryRead_Values_SurviveDocumentDisposal()
    {
        IReadOnlyDictionary<string, object?>? parameters;
        using (var doc = JsonDocument.Parse("""{"parameters":{"s":"text","ids":[1,2,3],"n":null}}"""))
        {
            McpSqlParameters.TryRead(doc.RootElement, out parameters).ShouldBeTrue();
        }

        parameters.ShouldNotBeNull();
        ((JsonElement)parameters["s"]!).GetString().ShouldBe("text");
        ((JsonElement)parameters["ids"]!).GetArrayLength().ShouldBe(3);
        ((JsonElement)parameters["n"]!).ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TryRead_ParametersHash_EqualsWebSqlBodyBindingHash()
    {
        const string body = """{"s":"text","ids":[1,2,3],"n":null,"b":true,"d":1.5}""";
        var web = JsonSerializer.Deserialize<Dictionary<string, object?>>(body);
        Read("{\"parameters\":" + body + "}", out var mcp).ShouldBeTrue();

        WebSqlPageTokenCodec.ComputeParametersHash(mcp).ShouldBe(WebSqlPageTokenCodec.ComputeParametersHash(web));
    }
}
