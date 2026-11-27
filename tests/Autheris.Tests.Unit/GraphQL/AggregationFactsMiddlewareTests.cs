using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.FlatAggregateTestSupport;
using static Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>The request middleware that moves flat aggregation facts into <c>extensions.autherisAggregation</c>.</summary>
[Collection("CatalogRelation")]
public sealed class AggregationFactsMiddlewareTests
{
    [Fact]
    public async Task NoFlatField_NoExtension()
    {
        var run = await RunAsync(new TypedAggregateTestHost(), $$"""
            { {{NestedRoot}}(aggregates: [{ fn: COUNT, alias: "n" }]) { count } }
            """);

        Errors(run.Result).ShouldBeEmpty();
        using var doc = Json(run.Result);
        HasFacts(doc).ShouldBeFalse();
    }

    [Fact]
    public async Task KeysSortedOrdinal_MergesWithMaskedFields()
    {
        var host = new TypedAggregateTestHost
        {
            ConfigureBuilder = builder => builder.UseRequest(next => async ctx =>
            {
                await next(ctx).ConfigureAwait(false);
                if (ctx.Result is OperationResult res)
                {
                    var ext = res.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
                    res.Extensions = ext.SetItem("maskedFields", new[] { "ssn" });
                }
            })
        };

        var run = await RunAsync(host, $"{{ b: {Root} {{ count }} B: {Root} {{ count }} a: {Root} {{ count }} }}");

        Errors(run.Result).ShouldBeEmpty();
        using var doc = Json(run.Result);
        Facts(doc).EnumerateObject().Select(p => p.Name).ShouldBe(["B", "a", "b"]);
        doc.RootElement.GetProperty("extensions").GetProperty("maskedFields")[0].GetString().ShouldBe("ssn");
    }
}
