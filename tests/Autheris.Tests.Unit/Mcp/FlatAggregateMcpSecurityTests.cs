namespace Autheris.Tests.Unit.Mcp;

using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Analytics;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using Autheris.Tests.Unit.GraphQL;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.FlatAggregateTestSupport;
using static Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost;

/// <summary>MCP <c>query_graphql</c> runs the flat <c>_groups</c> field through the same wrapper pipeline as the nested shape.</summary>
[Trait("Category", "Security")]
[Collection("CatalogRelation")]
public sealed class FlatAggregateMcpSecurityTests
{
    private static AggregationResult ThreeGroups(bool truncated) => Result(
        Enumerable.Range(0, 3).Select(i => Row(new Dictionary<string, object?> { ["region"] = "R" + i }, 5 + i, new Dictionary<string, object?> { ["agg_0"] = 5L + i })).ToList(),
        total: 18,
        meta: new AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, truncated));

    // MCP query_graphql returns the POST /graphql body unchanged (no row cap since #1208): every flat row and the
    // aggregation facts reach the caller as the service produced them, and a service-side truncation is never falsified.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpGraphQlBody_FlatRowsNotTrimmed_ExtensionsTruncatedNotFalsified(bool serviceTruncated)
    {
        var run = await RunAsync(new TypedAggregateTestHost(), $"{{ {Root} {{ region n: count }} }}", ThreeGroups(serviceTruncated));
        var body = JsonDocument.Parse(run.Result.ToJson());

        body.RootElement.GetProperty("data").GetProperty(Root).GetArrayLength().ShouldBe(3);
        body.RootElement.TryGetProperty("truncated", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("rowCap", out _).ShouldBeFalse();
        var fact = Facts(body).GetProperty(Root);
        fact.GetProperty("truncated").GetBoolean().ShouldBe(serviceTruncated);
        fact.GetProperty("count").GetInt64().ShouldBe(18);
    }

    [Fact]
    public async Task ReadOnlyToken_QueryGraphqlFlat_SameAsNested()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "reader"), new Claim("tenant_id", "T1")], "TestAuth");
        TokenAccessScope.MarkReadOnly(identity);
        var principal = new ClaimsPrincipal(identity);

        async Task<FlatAggregateTestSupport.Run> RunAsReader(string query)
        {
            var host = new TypedAggregateTestHost { User = principal, ConfigureBuilder = b => b.UseRequest<ReadOnlyOperationMiddleware>() };
            return await RunAsync(host, query, ThreeGroups(false));
        }

        var flat = await RunAsReader($"{{ {Root} {{ region n: count }} }}");
        var nested = await RunAsReader(
            $"{{ {NestedRoot}(groupBy: [region], aggregates: [{{ fn: COUNT, alias: \"agg_0\" }}]) {{ count }} }}");

        Errors(flat.Result).ShouldBeEmpty();
        Errors(nested.Result).ShouldBeEmpty();
        flat.Queries.Count.ShouldBe(1);
        nested.Queries.Count.ShouldBe(1);
        flat.Captured!.Table.ShouldBe(nested.Captured!.Table);
        flat.Captured.GroupBy.ShouldBe(nested.Captured.GroupBy);
        flat.Captured.Transport.ShouldBe(nested.Captured.Transport);
    }
}
