using System.Text.Json;
using Autheris.GraphQL.Interceptors;
using HotChocolate.Execution;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class DatasetFactsRequestTests
{
    [Theory]
    [InlineData("""{"autheris":{"datasetFacts":true}}""", true)]
    [InlineData("""{"autheris":{"datasetFacts":false}}""", false)]
    [InlineData("""{"autheris":{"datasetFacts":"true"}}""", false)]
    [InlineData("""{"autheris":{"datasetFacts":1}}""", false)]
    [InlineData("""{"autheris":{"datasetFacts":null}}""", false)]
    [InlineData("""{"autheris":{}}""", false)]
    [InlineData("""{"autheris":true}""", false)]
    [InlineData("""{"other":{"datasetFacts":true}}""", false)]
    public void JsonExtension_OnlyTheLiteralTrueEnables(string json, bool expected)
    {
        var request = OperationRequestBuilder.New().SetDocument("{ __typename }").SetExtensions(JsonDocument.Parse(json)).Build();

        DatasetFactsRequest.IsRequested(request).ShouldBe(expected);
    }

    [Fact]
    public void DictionaryExtension_TrueEnables()
    {
        var request = OperationRequestBuilder.New().SetDocument("{ __typename }")
            .SetExtensions(new Dictionary<string, object?> { ["autheris"] = new Dictionary<string, object?> { ["datasetFacts"] = true } }).Build();

        DatasetFactsRequest.IsRequested(request).ShouldBeTrue();
    }

    [Fact]
    public void NoExtensions_IsNotRequested()
    {
        DatasetFactsRequest.IsRequested(OperationRequestBuilder.New().SetDocument("{ __typename }").Build()).ShouldBeFalse();
    }
}
