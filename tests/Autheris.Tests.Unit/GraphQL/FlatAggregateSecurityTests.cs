using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Analytics;
using Autheris.Domain.Common;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.FlatAggregateTestSupport;
using static Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// Security conditions of the flat <c>&lt;table&gt;_groups</c> field: one governed path, fail-closed selection reading, collision-free
/// names and per-request facts.
/// </summary>
[Trait("Category", "Security")]
[Collection("CatalogRelation")]
public sealed class FlatAggregateSecurityTests
{
    private static string CatalogDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
        {
            current = current.Parent;
        }

        return Path.Combine((current ?? throw new InvalidOperationException("Autheris.sln not found.")).FullName, "src", "Autheris.GraphQL");
    }

    private static string[] FlatSourceFiles() =>
        [.. Directory.GetFiles(Path.Combine(CatalogDirectory(), "Catalog"), "Flat*.cs"),
         Path.Combine(CatalogDirectory(), "Interceptors", "AggregationFactsMiddleware.cs")];

    private static string TypeBlock(string sdl, string name)
    {
        var match = Regex.Match(sdl, $@"type {Regex.Escape(name)}\b[^{{]*\{{(?<body>[^}}]*)\}}", RegexOptions.Singleline);
        match.Success.ShouldBeTrue($"type {name} not found");
        return match.Groups["body"].Value;
    }

    private static string? ErrorCode(Action act) => CodeOf(Should.Throw<HotChocolate.GraphQLException>(act));

    private static IReadOnlyList<(string, string?, string?)> Shape(AggregationQuery q) =>
        q.Aggregates!.Select(a => (a.Function.ToString(), a.Column, a.Alias)).ToList();

    [Fact]
    public void FlatField_ReachesDataOnlyThroughGovernedService()
    {
        string[] forbidden =
        [
            "Repository", "ISqlExecutor", "IPolicyDecisionPoint", "SqlBuilder", "IDataSourceExecutor", "IGovernedTreeQueryService",
            "ISemanticMetricQueryService", "DbConnection", "SqlConnection", "ExecuteReader"
        ];
        var files = FlatSourceFiles();

        files.Length.ShouldBeGreaterThanOrEqualTo(5);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
            {
                text.ShouldNotContain(token, customMessage: Path.GetFileName(file));
            }
        }

        File.ReadAllText(Path.Combine(CatalogDirectory(), "Catalog", "FlatAggregateGraphQlTypes.cs")).ShouldContain("AggregateExecutionCore.ExecuteAsync");
        File.ReadAllText(Path.Combine(CatalogDirectory(), "Catalog", "AggregateExecutionCore.cs")).ShouldContain("IGovernedAggregationService");
    }

    [Fact]
    public async Task Unauthenticated_Unauthorized_BeforeSelectionRead_NoServiceCall()
    {
        var host = new TypedAggregateTestHost { User = new ClaimsPrincipal(new ClaimsIdentity()) };

        // Without an aggregate this selection would be ARGUMENT_INVALID if it were read; authentication answers first.
        var run = await RunAsync(host, $"{{ {Root} {{ region }} }}");

        Errors(run.Result).Select(e => e.Code).ShouldBe(["UNAUTHORIZED"]);
        run.Queries.ShouldBeEmpty();
    }

    [Fact]
    public async Task RowColumnResolver_DoesNotUseEntityResolvers_ReadsOnlyMappedRow()
    {
        var source = File.ReadAllText(Path.Combine(CatalogDirectory(), "Catalog", "FlatAggregateGraphQlTypes.cs"));
        foreach (var token in new[] { "ResolveRootQueryAsync", "RecordMaskedColumn", "ReportMasked", "ctx.Service<", "ctx.Parent<IReadOnlyDictionary" })
        {
            source.ShouldNotContain(token);
        }

        var run = await RunAsync(new TypedAggregateTestHost(), $"{{ {Root} {{ region n: count }} }}",
            Result([Row(new Dictionary<string, object?> { ["region"] = "from-the-row" }, 2, new Dictionary<string, object?> { ["agg_0"] = 2L })], 2));

        Errors(run.Result).ShouldBeEmpty();
        using var doc = Json(run.Result);
        doc.RootElement.GetProperty("data").GetProperty(Root)[0].GetProperty("region").GetString().ShouldBe("from-the-row");
    }

    [Fact]
    public async Task MaskedGroupColumn_MapsToAggregationOnMaskedColumn_NoRows()
    {
        var host = new TypedAggregateTestHost();
        var queries = new List<AggregationQuery>();
        host.Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Do<AggregationQuery>(queries.Add), Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new MaskedAggregationException("secret_column"));

        var run = await ExecuteAsync(host, $"{{ {Root} {{ region count }} }}", null, queries);

        Errors(run.Result).Select(e => e.Code).ShouldBe(["AGGREGATION_ON_MASKED_COLUMN"]);
        run.Captured!.GroupBy.ShouldBe(["region"]);
        using var doc = Json(run.Result);
        doc.RootElement.GetProperty("data").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task MaskedAggregateColumn_MapsToAggregationOnMaskedColumn()
    {
        var host = new TypedAggregateTestHost();
        var queries = new List<AggregationQuery>();
        host.Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Do<AggregationQuery>(queries.Add), Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new MaskedAggregationException("secret_column"));

        var run = await ExecuteAsync(host, $"{{ {Root} {{ amount_sum }} }}", null, queries);

        Errors(run.Result).Select(e => e.Code).ShouldBe(["AGGREGATION_ON_MASKED_COLUMN"]);
        run.Captured!.Aggregates!.Select(a => a.Column).ShouldBe(["amount"]);
    }

    [Fact]
    public void UnknownRowField_ArgumentInvalid_NotSkipped()
    {
        var table = SalesTable();

        foreach (var field in new[] { "bogus", "__schema", "region_sum", "amount_median", string.Empty })
        {
            ErrorCode(() => FlatAggregateSelectionReader.Read(Items(("k", field), ("n", "count")), table, NoVariables))
                .ShouldBe(AggregationErrorCodes.ArgumentInvalid);
        }
    }

    [Fact]
    public async Task SkippedByDirective_LiteralAndVariable_NotGroupedNotAggregated()
    {
        var run = await RunAsync(new TypedAggregateTestHost(), """
            query($s: Boolean!, $i: Boolean!) {
              sales_dbo_orders_groups {
                region @skip(if: true)
                qty @include(if: false)
                id @skip(if: $s)
                big @include(if: $i)
                amount_sum @skip(if: $s)
                amount_avg @include(if: $i)
                n: count
              }
            }
            """, variables: new() { ["s"] = true, ["i"] = false });

        Errors(run.Result).ShouldBeEmpty();
        run.Captured!.GroupBy.ShouldBeEmpty();
        Shape(run.Captured).ShouldBe([("Count", null, "agg_0")]);
    }

    [Fact]
    public async Task FragmentsAndInlineFragments_SameGroupSetAsPlain()
    {
        var plain = await RunAsync(new TypedAggregateTestHost(), $"{{ {Root} {{ region qty s: amount_sum n: count }} }}");
        var fragments = await RunAsync(new TypedAggregateTestHost(), """
            { sales_dbo_orders_groups { ...A ... on sales_dbo_orders_group_row { s: amount_sum ...B } } }
            fragment A on sales_dbo_orders_group_row { region }
            fragment B on sales_dbo_orders_group_row { qty n: count }
            """);

        Errors(plain.Result).ShouldBeEmpty();
        Errors(fragments.Result).ShouldBeEmpty();
        fragments.Captured!.GroupBy!.OrderBy(c => c).ShouldBe(plain.Captured!.GroupBy!.OrderBy(c => c));
        Shape(fragments.Captured).Select(s => (s.Item1, s.Item2)).OrderBy(s => s.Item1).ShouldBe(
            Shape(plain.Captured).Select(s => (s.Item1, s.Item2)).OrderBy(s => s.Item1));
    }

    [Fact]
    public async Task OnlyAggregateFields_WholeTableAggregate_SameQueryAsNestedNoGroupBy()
    {
        var flat = await RunAsync(new TypedAggregateTestHost(), $"{{ {Root} {{ n: count s: amount_sum }} }}");
        var nested = await RunAsync(new TypedAggregateTestHost(), $$"""
            { {{NestedRoot}}(aggregates: [{ fn: COUNT, alias: "agg_0" }, { fn: SUM, column: amount, alias: "agg_1" }]) { count } }
            """);

        Errors(flat.Result).ShouldBeEmpty();
        flat.Captured!.GroupBy.ShouldBeEmpty();
        nested.Captured!.GroupBy.ShouldBeEmpty();
        Shape(flat.Captured).ShouldBe(Shape(nested.Captured));
        flat.Captured.Limit.ShouldBe(nested.Captured.Limit);
        flat.Captured.Transport.ShouldBe(nested.Captured.Transport);
    }

    [Fact]
    public async Task OnlyGroupColumns_ZeroAggregates_ArgumentInvalid()
    {
        foreach (var selection in new[] { "region", "region _isMergedOther", "__typename" })
        {
            var run = await RunAsync(new TypedAggregateTestHost(), $"{{ {Root} {{ {selection} }} }}");

            Errors(run.Result).Select(e => e.Code).ShouldBe([AggregationErrorCodes.ArgumentInvalid]);
            run.Queries.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task SameColumnTwoKeys_DifferentBuckets_ArgumentInvalid_SameArgs_OneGroup()
    {
        var different = await RunAsync(new TypedAggregateTestHost(),
            $"{{ {Root} {{ a: created(bucket: DAY) b: created(bucket: MONTH) count }} }}");
        var viaVariable = await RunAsync(new TypedAggregateTestHost(),
            $"query($b: TimeBucketInterval) {{ {Root} {{ a: created(bucket: DAY) b: created(bucket: $b) count }} }}",
            variables: new() { ["b"] = "MONTH" });
        var same = await RunAsync(new TypedAggregateTestHost(),
            $"query($b: TimeBucketInterval) {{ {Root} {{ a: created(bucket: DAY) b: created(bucket: $b) count }} }}",
            variables: new() { ["b"] = "DAY" });

        Errors(different.Result).Select(e => e.Code).ShouldBe([AggregationErrorCodes.ArgumentInvalid]);
        Errors(viaVariable.Result).Select(e => e.Code).ShouldBe([AggregationErrorCodes.ArgumentInvalid]);
        different.Queries.ShouldBeEmpty();
        Errors(same.Result).ShouldBeEmpty();
        same.Captured!.GroupBy.ShouldBe(["created"]);
        same.Captured.TimeBuckets.ShouldNotBeNull().ShouldHaveSingleItem().Column.ShouldBe("created");
    }

    [Fact]
    public async Task SameAggregateTwoKeys_ComputedOnce_NoCDuplicateRuleHit()
    {
        var result = Result([Row(new Dictionary<string, object?>(), 4, new Dictionary<string, object?> { ["agg_0"] = 12.5m, ["agg_1"] = 4L })], 4);

        var run = await RunAsync(new TypedAggregateTestHost(), $"{{ {Root} {{ a: amount_sum b: amount_sum n: count }} }}", result);

        Errors(run.Result).ShouldBeEmpty();
        Shape(run.Captured!).ShouldBe([("Sum", "amount", "agg_0"), ("Count", null, "agg_1")]);
        using var doc = Json(run.Result);
        var row = doc.RootElement.GetProperty("data").GetProperty(Root)[0];
        row.GetProperty("a").GetDecimal().ShouldBe(12.5m);
        row.GetProperty("b").GetDecimal().ShouldBe(12.5m);
    }

    [Fact]
    public async Task ResponseKeys_NeverReachQueryOrAudit_ColumnNamedKeys()
    {
        var run = await RunAsync(new TypedAggregateTestHost(), $$"""
            { {{Root}}(orderBy: [{ alias: "leak_key_two", direction: ASC }]) {
                leak_key_one: region leak_key_two: amount_sum count: count amount: region_count_distinct } }
            """);

        Errors(run.Result).ShouldBeEmpty();
        var query = run.Captured!;
        query.OrderBy.ShouldBe([new TableOrderBy("agg_0", false)]);
        var sent = JsonSerializer.Serialize(new { query.GroupBy, query.Aggregates, query.OrderBy });
        sent.ShouldNotContain("leak_key");
        query.Aggregates!.Select(a => a.Alias).ShouldBe(["agg_0", "agg_1", "agg_2"]);
        using var doc = Json(run.Result);
        doc.RootElement.GetProperty("data").GetProperty(Root).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task SyntheticAlias_AvoidsColumnsCountAndOtherAliases()
    {
        var host = new TypedAggregateTestHost
        {
            Tables = [DefaultTable("orders", ("id", "int"), ("agg_0", "int"), ("agg_1", "int"), ("region", "varchar(50)"))]
        };

        var run = await RunAsync(host, $"{{ {Root} {{ a: count b: agg_0_sum c: region_count_distinct }} }}");

        Errors(run.Result).ShouldBeEmpty();
        var aliases = run.Captured!.Aggregates!.Select(a => a.Alias!).ToList();
        aliases.ShouldBe(["agg_0_", "agg_1_", "agg_2"]);
        aliases.ShouldBeUnique();
        aliases.ShouldAllBe(a => SqlIdentifierGuard.IsValidIdentifier(a, 64) && a != "count" && !a.StartsWith("__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NameCollisions_ColumnCount_IsMergedOther_SumSuffix_Deterministic_NoThrow()
    {
        TypedAggregateTestHost Host() => new()
        {
            Tables = [DefaultTable("orders", ("id", "int"), ("count", "int"), ("amt", "decimal(18,2)"), ("amt_sum", "decimal(18,2)"))]
        };

        var first = (await Host().BuildAsync()).Schema.ToString();
        var second = (await Host().BuildAsync()).Schema.ToString();
        var block = TypeBlock(first, "sales_dbo_orders_group_row");

        TypeBlock(second, "sales_dbo_orders_group_row").ShouldBe(block);
        block.ShouldContain("count: Int");
        block.ShouldContain("count_2: Long!");
        block.ShouldContain("amt_sum: Decimal");
        block.ShouldContain("amt_sum_2: Decimal");

        var run = await RunAsync(Host(), $"{{ {Root} {{ count amt_sum x: count_2 y: amt_sum_2 }} }}");
        Errors(run.Result).ShouldBeEmpty();
        run.Captured!.GroupBy.ShouldBe(["count", "amt_sum"]);
        Shape(run.Captured).ShouldBe([("Count", null, "agg_0"), ("Sum", "amt", "agg_1")]);
    }

    [Fact]
    public async Task TableNamedGroupsOrGroupRow_OmittedWithNameCollision()
    {
        foreach (var clash in new[] { "orders_groups", "orders_group_row" })
        {
            var host = new TypedAggregateTestHost { Tables = [DefaultTable("orders"), DefaultTable(clash)] };

            var sdl = (await host.BuildAsync()).Schema.ToString();

            sdl.ShouldContain("sales_dbo_orders_groups(");
            sdl.ShouldContain("[sales_dbo_orders_group_row!]!");
            sdl.ShouldNotContain("type sales_dbo_orders_groups ");
            sdl.ShouldNotContain("type sales_dbo_orders_group_row_");
            sdl.ShouldNotContain($"sales_dbo_{clash}_aggregate");
        }
    }

    [Fact]
    public async Task Facts_CopiedFromDto_ExposeSuppressedOff_ZeroSuppressedCount()
    {
        var host = new TypedAggregateTestHost();
        host.Analytics.Privacy.ExposeSuppressedGroupsCount.ShouldBeFalse();
        var meta = new AggregationMetadata(5, SmallGroupStrategy.Suppress, true, 0, 0, false, true);
        var result = Result(
            [Row(new Dictionary<string, object?> { ["region"] = "EU" }, 4, new Dictionary<string, object?> { ["agg_0"] = 4L })], total: 99, meta);

        var flat = await RunAsync(host, $"{{ {Root} {{ region count }} }}", result);
        var nested = await RunAsync(new TypedAggregateTestHost(), $$"""
            { {{NestedRoot}}(groupBy: [region], aggregates: [{ fn: COUNT, alias: "n" }]) {
                count suppressed suppressedGroupsCount minimumGroupSize minimumGroupSizeSource mergedIntoOther truncated } }
            """, result);

        Errors(flat.Result).ShouldBeEmpty();
        using var flatDoc = Json(flat.Result);
        using var nestedDoc = Json(nested.Result);
        var facts = Facts(flatDoc).GetProperty(Root);
        var expected = nestedDoc.RootElement.GetProperty("data").GetProperty(NestedRoot);
        facts.GetProperty("count").GetInt64().ShouldBe(99);
        foreach (var name in new[] { "count", "suppressed", "suppressedGroupsCount", "minimumGroupSize", "minimumGroupSizeSource", "mergedIntoOther", "truncated" })
        {
            facts.GetProperty(name).ToString().ShouldBe(expected.GetProperty(name).ToString(), name);
        }

        facts.GetProperty("suppressedGroupsCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Facts_NotWrittenWhenFieldFails()
    {
        var failing = new TypedAggregateTestHost();
        failing.Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new MaskedAggregationException("secret_column"));
        var executor = await failing.BuildAsync();
        var thrown = await executor.ExecuteAsync($"{{ {Root} {{ count }} }}");

        var mismatch = await RunAsync(new TypedAggregateTestHost(), $"{{ {Root} {{ n: count }} }}",
            Result([Row(new Dictionary<string, object?>(), 1, new Dictionary<string, object?> { ["agg_0"] = "not a number" })], 1));

        Errors(thrown).ShouldNotBeEmpty();
        Errors(mismatch.Result).Select(e => e.Code).ShouldBe([AggregationErrorCodes.ResultTypeMismatch]);
        using var thrownDoc = Json(thrown);
        using var mismatchDoc = Json(mismatch.Result);
        HasFacts(thrownDoc).ShouldBeFalse();
        HasFacts(mismatchDoc).ShouldBeFalse();
    }

    [Fact]
    public async Task Facts_PerResponseKey_TwoFieldsOneOperation_NoCrossLeak()
    {
        var host = new TypedAggregateTestHost();
        host.Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(Result([], total: (ci.ArgAt<AggregationQuery>(1).GroupBy?.Count ?? 0) == 0 ? 100 : 7)));

        var run = await ExecuteAsync(host, $"{{ whole: {Root} {{ count }} grouped: {Root} {{ region count }} }}", null, []);

        Errors(run.Result).ShouldBeEmpty();
        using var doc = Json(run.Result);
        Facts(doc).GetProperty("whole").GetProperty("count").GetInt64().ShouldBe(100);
        Facts(doc).GetProperty("grouped").GetProperty("count").GetInt64().ShouldBe(7);
    }

    [Fact]
    public async Task Facts_TwoSequentialRequests_NoCrossLeak()
    {
        var host = new TypedAggregateTestHost();
        long total = 5;
        host.Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Result([], total)));
        var executor = await host.BuildAsync();

        var first = await executor.ExecuteAsync($"{{ one: {Root} {{ count }} }}");
        total = 9;
        var second = await executor.ExecuteAsync($"{{ two: {Root} {{ count }} }}");
        var third = await executor.ExecuteAsync($"{{ {NestedRoot}(aggregates: [{{ fn: COUNT, alias: \"n\" }}]) {{ count }} }}");

        using var firstDoc = Json(first);
        using var secondDoc = Json(second);
        using var thirdDoc = Json(third);
        Facts(firstDoc).EnumerateObject().Select(p => p.Name).ShouldBe(["one"]);
        Facts(secondDoc).EnumerateObject().Select(p => p.Name).ShouldBe(["two"]);
        Facts(secondDoc).GetProperty("two").GetProperty("count").GetInt64().ShouldBe(9);
        HasFacts(thirdDoc).ShouldBeFalse();
    }

    [Fact]
    public async Task NoBag_Throws_NoRows()
    {
        var host = new TypedAggregateTestHost { InstallFactsMiddleware = false };

        var run = await RunAsync(host, $"{{ {Root} {{ count }} }}");

        Errors(run.Result).Select(e => e.Code).ShouldBe([AggregationErrorCodes.NotSupported]);
        run.Queries.ShouldBeEmpty();
        using var doc = Json(run.Result);
        doc.RootElement.GetProperty("data").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task FactsMiddleware_NonOperationResult_NoThrowNoExtension()
    {
        var host = new TypedAggregateTestHost
        {
            ConfigureBuilder = builder => builder.UseRequest(next => async ctx =>
            {
                await next(ctx).ConfigureAwait(false);
                var bag = (ConcurrentDictionary<string, AggregationFacts>)ctx.ContextData[AggregationFactsMiddleware.ContextKey]!;
                bag["x"] = new AggregationFacts(1, false, 0, 5, "default", false, false);
                ctx.Result = new OperationResultBatch(ImmutableList<IExecutionResult>.Empty);
            })
        };
        var executor = await host.BuildAsync();

        var result = await executor.ExecuteAsync("{ __typename }");

        result.ShouldBeOfType<OperationResultBatch>();
    }
}
