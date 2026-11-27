using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Analytics;
using Autheris.Domain.Model;
using HotChocolate.Execution;
using NSubstitute;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Language;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>Builders shared by the flat aggregation tests: a catalog table without a schema build, and the error code of a failure.</summary>
internal static class FlatAggregateTestSupport
{
    public static readonly Func<string, IValueNode?> NoVariables = _ => null;

    public static CatalogTableType Table(params (string Field, CatalogFieldType Type)[] columns) => new(
        new TableIdentifier("sales", "dbo", "orders"),
        "sales_dbo_orders",
        "sales_dbo_orders",
        "sales_dbo_orders_filter",
        "sales_dbo_orders_order",
        TypedAggregateTestHost.DefaultTable(),
        columns.Select(c => new CatalogColumnField(c.Field, c.Field, c.Type, "x", true)).ToList(),
        []);

    public static CatalogTableType SalesTable() => Table(
        ("region", CatalogFieldType.String),
        ("amount", CatalogFieldType.Decimal),
        ("qty", CatalogFieldType.Int),
        ("created", CatalogFieldType.DateTime),
        ("is_vip", CatalogFieldType.Boolean));

    public const string Root = "sales_dbo_orders_groups";

    public const string NestedRoot = "sales_dbo_orders_aggregate";

    /// <summary>The response of one run and every query the governed service received (several fields give several queries).</summary>
    public sealed record Run(IExecutionResult Result, IReadOnlyList<AggregationQuery> Queries)
    {
        public AggregationQuery? Captured => Queries.Count == 0 ? null : Queries[^1];
    }

    public static AggregationGroupRow Row(Dictionary<string, object?> key, long count, Dictionary<string, object?> aggs, bool merged = false) =>
        new(key, count, aggs, merged);

    public static async Task<Run> RunAsync(
        TypedAggregateTestHost host,
        string query,
        AggregationResult? result = null,
        Dictionary<string, object?>? variables = null)
    {
        var queries = new List<AggregationQuery>();
        host.Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Do<AggregationQuery>(queries.Add), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(result ?? TypedAggregateTestHost.Result([])));
        return await ExecuteAsync(host, query, variables, queries);
    }

    public static async Task<Run> ExecuteAsync(
        TypedAggregateTestHost host, string query, Dictionary<string, object?>? variables, List<AggregationQuery> queries)
    {
        var executor = await host.BuildAsync();
        var request = OperationRequestBuilder.New().SetDocument(query);
        if (variables != null)
        {
            request.SetVariableValues(variables);
        }

        return new Run(await executor.ExecuteAsync(request.Build()), queries);
    }

    public static JsonElement Facts(JsonDocument doc) =>
        doc.RootElement.GetProperty("extensions").GetProperty("autherisAggregation");

    public static bool HasFacts(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("autherisAggregation", out _);

    public static string? CodeOf(GraphQLException ex) => ex.Errors[0].Code;

    public static IReadOnlyList<(string ResponseKey, string FieldName, IValueNode? Bucket, IValueNode? TimeZone)> Items(
        params (string ResponseKey, string FieldName)[] items) =>
        items.Select(i => (i.ResponseKey, i.FieldName, (IValueNode?)null, (IValueNode?)null)).ToList();
}
