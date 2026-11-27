namespace Autheris.Tests.Unit.Sql.ResultCache;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autheris.Application.Common.Quota;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.ResultCache;
using Autheris.Application.Sql.Services;
using Shouldly;
using Xunit;

/// <summary>The result cache for paged requests: one full execution serves every page, and every page keeps the uncached page contract.</summary>
public sealed class GovernedSqlExecutorResultCachePagingTests
{
    [Fact]
    public async Task Pages_one_to_n_run_the_source_once_with_the_uncached_page_contract()
    {
        using var pipeline = new GovernedResultCachePipeline(rows: 5);
        using var uncached = new GovernedResultCachePipeline(rows: 5);

        var plain = await uncached.RunAsync(page: new SqlPageWindow(0, 2, 1), participate: false);
        var page1 = await pipeline.RunAsync(page: new SqlPageWindow(0, 2, 1));
        var page2 = await pipeline.RunAsync(nextToken: page1.Page!.NextToken);
        var page3 = await pipeline.RunAsync(nextToken: page2.Page!.NextToken);

        page1.Rows.ShouldBe(plain.Rows);
        page1.Rows.ShouldBe(["0:name0", "1:name1"]);
        page2.Rows.ShouldBe(["2:name2", "3:name3"]);
        page3.Rows.ShouldBe(["4:name4"]);
        pipeline.Executions.ShouldBe(1);

        (page1.Page!.Page!.Number, page1.Page.Page.Offset, page1.Page.Page.Size).ShouldBe((1, 0L, 2));
        (page2.Page!.Page!.Number, page2.Page.Page.Offset, page2.Page.Page.Size).ShouldBe((2, 2L, 2));
        (page3.Page!.Page!.Number, page3.Page.Page.Offset, page3.Page.Page.Size).ShouldBe((3, 4L, 2));
        plain.Page!.NextToken.ShouldNotBeNull();
        page1.Page.NextToken.ShouldNotBeNull();
        page2.Page.NextToken.ShouldNotBeNull();
        page3.Page.NextToken.ShouldBeNull();
        page3.Page.PagingUnavailable.ShouldBeNull();

        page1.Outcomes.Select(o => o.Status).ShouldBe([WebSqlResultCacheStatus.Miss, WebSqlResultCacheStatus.Store]);
        page2.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        page3.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_page_ordered_by_the_key_fills_then_hits_with_and_without_a_declared_key(bool declaredKey)
    {
        using var pipeline = new GovernedResultCachePipeline(rows: 75, maxOffset: 20, primaryKey: declaredKey ? ["id"] : null);
        const string sql = GovernedResultCachePipeline.Sql;

        var first = await pipeline.RunAsync(sql: sql, page: new SqlPageWindow(0, 10, 1));
        var again = await pipeline.RunAsync(sql: sql, page: new SqlPageWindow(0, 10, 1));

        first.Outcomes.Select(o => o.Status).ShouldBe([WebSqlResultCacheStatus.Miss, WebSqlResultCacheStatus.Store]);
        again.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        again.Rows.Count.ShouldBe(10);
        pipeline.Executions.ShouldBe(1);
    }

    [Fact]
    public async Task Last_full_page_has_no_next_token()
    {
        using var pipeline = new GovernedResultCachePipeline(rows: 4);

        var page1 = await pipeline.RunAsync(page: new SqlPageWindow(0, 2, 1));
        var page2 = await pipeline.RunAsync(nextToken: page1.Page!.NextToken);

        page2.Rows.ShouldBe(["2:name2", "3:name3"]);
        page2.Page!.NextToken.ShouldBeNull();
        pipeline.Executions.ShouldBe(1);
    }

    [Fact]
    public async Task Too_large_result_leaves_a_tombstone_and_later_pages_run_per_page()
    {
        using var pipeline = new GovernedResultCachePipeline(rows: 5, maxRowsPerEntry: 3);

        var page1 = await pipeline.RunAsync(page: new SqlPageWindow(0, 2, 1));
        var page2 = await pipeline.RunAsync(nextToken: page1.Page!.NextToken);

        page1.Rows.ShouldBe(["0:name0", "1:name1"]);
        page1.Page.NextToken.ShouldNotBeNull();
        page1.Outcomes.Last().Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        page1.Outcomes.Last().Reason.ShouldBe(WebSqlResultCacheReasons.TooLarge);
        page2.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Ineligible);
        page2.Outcomes[0].Reason.ShouldBe(WebSqlResultCacheReasons.TooLarge);
        pipeline.Executions.ShouldBe(2);
        pipeline.Cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Paged_second_rewrite_denied_falls_back_uncached_SECRC22()
    {
        var threshold = int.MaxValue;
        var quota = new ScriptedCostQuota(request => request.EstimatedCost > threshold ? QueryCostOutcome.OverCost : QueryCostOutcome.Admitted);
        var estimates = quota.Estimates;
        using var pipeline = new GovernedResultCachePipeline(rows: 5, costQuota: quota);

        await pipeline.RunAsync(page: new SqlPageWindow(0, 2, 1), participate: false);
        threshold = estimates[0];
        pipeline.Cache.EntryCount.ShouldBe(0);
        var run = await pipeline.RunAsync(page: new SqlPageWindow(0, 2, 1));

        estimates.Count.ShouldBeGreaterThanOrEqualTo(3);
        estimates.ShouldContain(e => e > threshold);
        run.Rows.ShouldBe(["0:name0", "1:name1"]);
        run.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Ineligible);
        run.Outcomes[0].Reason.ShouldBe(WebSqlResultCacheReasons.PagingUnavailable);
        run.Page!.NextToken.ShouldNotBeNull();
        pipeline.Spy.Lookups.ShouldBe(0);
        pipeline.Cache.EntryCount.ShouldBe(0);
        pipeline.Executions.ShouldBe(2);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(5, 5, 5)]
    [InlineData(5, 2, 2)]
    [InlineData(5, 0, 5)]
    [InlineData(1, 3, 1)]
    public void Paged_slice_capped_by_first_rewrite_row_limit_SECRC22(int pageSize, long firstRewriteLimit, int expected) =>
        GovernedSqlExecutor.ResultCacheWindowSize(pageSize, firstRewriteLimit).ShouldBe(expected);

    [Fact]
    [Trait("Category", "Security")]
    public async Task Paged_pages_of_another_user_never_share_the_full_result()
    {
        using var pipeline = new GovernedResultCachePipeline(rows: 5);

        var first = await pipeline.RunAsync(GovernedResultCachePipeline.User("user-1"), page: new SqlPageWindow(0, 2, 1));
        var other = await pipeline.RunAsync(GovernedResultCachePipeline.User("user-2"), page: new SqlPageWindow(0, 2, 1));

        first.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        other.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        pipeline.Executions.ShouldBe(2);
    }
}
