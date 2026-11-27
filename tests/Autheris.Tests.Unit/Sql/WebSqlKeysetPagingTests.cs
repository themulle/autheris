namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Application.Common.Quota;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.ResultCache;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Sql.ResultCache;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>Keyset continuation of paged WebSQL statements beyond the offset limit, against the full executor pipeline.</summary>
public sealed class WebSqlKeysetPagingTests
{
    /// <summary>A distinctive first key so a leaked key value cannot be mistaken for another number.</summary>
    internal const int Base = 7_345_000;

    private const string OrderByName = "SELECT id, name FROM crm.customers ORDER BY name";

    internal static GovernedResultCachePipeline Create(
        int rows = 10,
        long maxOffset = 4,
        IReadOnlyList<string>? primaryKey = null,
        Microsoft.Extensions.Logging.ILogger<GovernedSqlExecutionService>? logger = null,
        IQueryCostQuota? costQuota = null)
    {
        var pipeline = new GovernedResultCachePipeline(rows: rows, maxOffset: maxOffset, primaryKey: primaryKey ?? ["id"], logger: logger, costQuota: costQuota);
        pipeline.SourceFor = (_, values) => SeekRows(rows, values);
        return pipeline;
    }

    /// <summary>The source as a database answers it: every row after the bound seek key (an int bind), else the whole table.</summary>
    internal static DbDataReader SeekRows(int total, object?[] values)
    {
        int? after = values.OfType<int>().Cast<int?>().LastOrDefault();
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("name", typeof(string));
        for (var i = 0; i < total; i++)
        {
            if (after is null || Base + i > after)
            {
                table.Rows.Add(Base + i, $"name{i:000}");
            }
        }

        return table.CreateDataReader();
    }

    private static async Task<List<CachePipelineRun>> WalkAsync(GovernedResultCachePipeline pipeline, int size = 2, string sql = GovernedResultCachePipeline.Sql)
    {
        var runs = new List<CachePipelineRun> { await pipeline.RunAsync(sql: sql, page: new SqlPageWindow(0, size, 1)) };
        while (runs[^1].Page!.NextToken is { } next && runs.Count < 50)
        {
            runs.Add(await pipeline.RunAsync(sql: sql, nextToken: next));
        }

        return runs;
    }

    [Fact]
    public async Task EligibleOrder_BelowMaxOffset_NextIsOffsetToken()
    {
        using var pipeline = Create(maxOffset: 10);

        var page1 = await pipeline.RunAsync(page: new SqlPageWindow(0, 2, 1));
        var page2 = await pipeline.RunAsync(nextToken: page1.Page!.NextToken);

        page2.Rows.Select(r => r.Split(':')[0]).ShouldBe([$"{Base + 2}", $"{Base + 3}"]);
        page2.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        pipeline.Commands.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task KeysetToken_NotIssuedBelowMaxOffset()
    {
        using var pipeline = Create(maxOffset: 4);

        var page1 = await pipeline.RunAsync(page: new SqlPageWindow(0, 2, 1));
        var page2 = await pipeline.RunAsync(nextToken: page1.Page!.NextToken);
        var page3 = await pipeline.RunAsync(nextToken: page2.Page!.NextToken);

        // pages up to the limit keep v1 tokens: the continuations are still sliced from the cached full result
        page2.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        page3.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        pipeline.Commands.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task KeysetToken_IssuedWhenNextOffsetExceedsMaxOffset()
    {
        using var pipeline = Create(maxOffset: 4);

        var runs = await WalkAsync(pipeline);

        runs[2].Page!.Page!.Offset.ShouldBe(4);
        runs[2].Page!.NextToken.ShouldNotBeNull();
        runs[3].Page!.Page!.Offset.ShouldBe(6);
        runs[3].Outcomes.ShouldHaveSingleItem().Reason.ShouldBe(WebSqlResultCacheReasons.KeysetPage);
    }

    [Fact]
    public async Task Continuation_ReadsBeyondMaxOffset_ToLastPage()
    {
        using var pipeline = Create(rows: 9, maxOffset: 4);

        var runs = await WalkAsync(pipeline);

        runs.Count.ShouldBe(5);
        runs.SelectMany(r => r.Rows).Select(r => int.Parse(r.Split(':')[0])).ShouldBe(Enumerable.Range(Base, 9));
        runs.Select(r => r.Page!.Page!.Number).ShouldBe([1, 2, 3, 4, 5]);
        runs[^1].Page!.NextToken.ShouldBeNull();
        runs[^1].Page!.PagingUnavailable.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Continuation_CompiledSqlHasSeekSlotsAndNoOffsetSlot()
    {
        using var pipeline = Create(maxOffset: 4);

        await WalkAsync(pipeline);

        var (sql, values) = pipeline.Commands.First(c => c.Values.OfType<int>().Any());
        sql.ShouldNotContain("OFFSET", Case.Insensitive);
        Regex.IsMatch(sql, @"LIMIT\s+3\b").ShouldBeTrue(sql);
        // the probe row (Base + 6) is never the seek key: the last row delivered on page 3 is
        values.OfType<int>().ShouldContain(Base + 5);
        values.OfType<int>().ShouldNotContain(Base + 6);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task KeysetSeek_ValuesOnlyInBoundSlots_NeverInSqlText()
    {
        using var pipeline = Create(maxOffset: 4);

        await WalkAsync(pipeline);

        var (sql, values) = pipeline.Commands.First(c => c.Values.OfType<int>().Any());
        values.ShouldContain(Base + 5);
        sql.ShouldNotContain((Base + 5).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task KeysetPage_ProbeLimitIsPageSizePlusOne_AndRowLimitHolds()
    {
        using var pipeline = Create(rows: 50, maxOffset: 4);

        var runs = await WalkAsync(pipeline);

        // the source hands back every remaining row; the page still delivers exactly its size and reports more
        runs[3].Rows.Count.ShouldBe(2);
        runs[3].Page!.NextToken.ShouldNotBeNull();
        Regex.IsMatch(pipeline.Commands.Last().Sql, @"LIMIT\s+3\b").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Ineligible_BeyondMaxOffset_ReturnsOffsetLimit()
    {
        using var pipeline = Create(maxOffset: 2);

        var page1 = await pipeline.RunAsync(sql: OrderByName, page: new SqlPageWindow(0, 2, 1));
        var page2 = await pipeline.RunAsync(sql: OrderByName, nextToken: page1.Page!.NextToken);

        page2.Page!.NextToken.ShouldBeNull();
        page2.Page.PagingUnavailable.ShouldBe("offset_limit");
    }

    [Fact]
    public async Task FederatedOrLimit_ReasonsUnchanged()
    {
        using var pipeline = Create();

        var limited = await pipeline.RunAsync(sql: "SELECT id, name FROM crm.customers ORDER BY id LIMIT 5", page: new SqlPageWindow(0, 2, 1));
        var unordered = await pipeline.RunAsync(sql: "SELECT id, name FROM crm.customers", page: new SqlPageWindow(0, 2, 1));

        limited.Page!.PagingUnavailable.ShouldBe("statement_has_limit");
        unordered.Page!.PagingUnavailable.ShouldBe("order_by_required");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UnsupportedKeyType_FallsBackToOffset()
    {
        using var pipeline = Create(maxOffset: 2);
        pipeline.SourceFor = (_, _) =>
        {
            var table = new DataTable();
            table.Columns.Add("id", typeof(int));
            table.Columns.Add("name", typeof(string));
            for (var i = 0; i < 6; i++)
            {
                table.Rows.Add(DBNull.Value, "n");
            }

            return table.CreateDataReader();
        };

        var run = await pipeline.RunAsync(page: new SqlPageWindow(2, 2, 2));

        run.Page!.NextToken.ShouldBeNull();
        run.Page.PagingUnavailable.ShouldBe("offset_limit");
    }

    [Fact]
    public async Task ExplicitPageNumber_RunsOffset_NextIsKeyset()
    {
        using var pipeline = Create(maxOffset: 2);

        var page2 = await pipeline.RunAsync(page: new SqlPageWindow(2, 2, 2));
        var page3 = await pipeline.RunAsync(nextToken: page2.Page!.NextToken);

        page2.Page.NextToken.ShouldNotBeNull();
        page3.Rows.Select(r => int.Parse(r.Split(':')[0])).ShouldBe([Base + 4, Base + 5]);
        page3.Page!.Page!.Offset.ShouldBe(4);
        page3.Outcomes.ShouldHaveSingleItem().Reason.ShouldBe(WebSqlResultCacheReasons.KeysetPage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task OffsetPage_BeyondMaxOffset_ViaPageNumber_StillRejected()
    {
        using var pipeline = Create(maxOffset: 2);

        Exception? thrown = null;
        try
        {
            await pipeline.RunAsync(page: new SqlPageWindow(4, 2, 3));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        thrown.ShouldNotBeNull();
        pipeline.Commands.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Page1_WithV2Token_StillCacheable()
    {
        using var pipeline = Create(maxOffset: 1);

        var page1 = await pipeline.RunAsync(page: new SqlPageWindow(0, 2, 1));

        page1.Page!.NextToken.ShouldNotBeNull();
        page1.Outcomes.Select(o => o.Status).ShouldBe([WebSqlResultCacheStatus.Miss, WebSqlResultCacheStatus.Store]);
    }

    [Fact]
    public async Task CacheHit_AtOffsetLimit_StillIssuesKeysetNext()
    {
        using var pipeline = Create(maxOffset: 2);

        await pipeline.RunAsync(page: new SqlPageWindow(0, 2, 1));
        var page2 = await pipeline.RunAsync(page: new SqlPageWindow(2, 2, 2));
        var page3 = await pipeline.RunAsync(nextToken: page2.Page!.NextToken);

        page2.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        page3.Rows.Select(r => int.Parse(r.Split(':')[0])).ShouldBe([Base + 4, Base + 5]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task KeysetPage_BypassesResultCache_ReasonKeysetPage()
    {
        using var pipeline = Create(maxOffset: 2);

        var page2 = await pipeline.RunAsync(page: new SqlPageWindow(2, 2, 2));
        var lookupsBefore = pipeline.Spy.Lookups;
        var page3 = await pipeline.RunAsync(nextToken: page2.Page!.NextToken);
        var page3Again = await pipeline.RunAsync(nextToken: page2.Page!.NextToken);

        page3.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Ineligible);
        page3.Outcomes[0].Reason.ShouldBe(WebSqlResultCacheReasons.KeysetPage);
        page3Again.Outcomes[0].Reason.ShouldBe(WebSqlResultCacheReasons.KeysetPage);
        pipeline.Spy.Lookups.ShouldBe(lookupsBefore);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PolicyNowMasksKey_ContinuationTokenInvalid()
    {
        using var pipeline = Create(maxOffset: 2);
        var page2 = await pipeline.RunAsync(page: new SqlPageWindow(2, 2, 2));
        page2.Page!.NextToken.ShouldNotBeNull();

        pipeline.Consent.Masked.Add("id");

        await Should.ThrowAsync<Exception>(async () => await pipeline.RunAsync(nextToken: page2.Page.NextToken));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CatalogPkChanged_ContinuationTokenInvalid()
    {
        using var pipeline = Create(maxOffset: 2);
        var page2 = await pipeline.RunAsync(page: new SqlPageWindow(2, 2, 2));
        page2.Page!.NextToken.ShouldNotBeNull();

        foreach (var schema in new[] { "crm", "dbo", "public" })
        {
            pipeline.Repo.Add(GovernedResultCachePipeline.CustomersTable(schema, ["name"]));
        }

        await Should.ThrowAsync<SqlPagingTokenInvalidException>(async () => await pipeline.RunAsync(nextToken: page2.Page.NextToken));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task KeysetPage_AuditedLikeOffsetPage_StatementHashFromClientStatement()
    {
        var quota = new ScriptedCostQuota();
        using var pipeline = Create(maxOffset: 2, costQuota: quota);

        var page2 = await pipeline.RunAsync(page: new SqlPageWindow(2, 2, 2), participate: false);
        var admissionsBefore = quota.Estimates.Count;
        await pipeline.RunAsync(nextToken: page2.Page!.NextToken, participate: false);

        quota.Estimates.Count.ShouldBeGreaterThan(admissionsBefore);
        var entries = pipeline.AuditEntries.Where(e => e.EventType == Autheris.Domain.Audit.AuditEventTypes.WebSqlQuery).ToList();
        var offsetEntry = entries[^2];
        var keysetEntry = entries[^1];
        keysetEntry.DetailsJson.ShouldContain("\"keyset\":true");
        offsetEntry.DetailsJson!.ShouldNotContain("\"keyset\"");
        HashOf(keysetEntry.DetailsJson!).ShouldBe(HashOf(offsetEntry.DetailsJson!));
    }

    private static string HashOf(string detailsJson) =>
        System.Text.Json.JsonDocument.Parse(detailsJson).RootElement.GetProperty("originalSqlHash").GetString()!;

    [Fact]
    [Trait("Category", "Security")]
    public async Task KeysetToken_NotLoggedOrAudited()
    {
        var logger = new CapturedLogger();
        using var pipeline = Create(maxOffset: 2, logger: logger);

        var runs = await WalkAsync(pipeline);

        var tokens = runs.Select(r => r.Page!.NextToken).Where(t => t is not null).Cast<string>().ToList();
        tokens.Count.ShouldBeGreaterThan(2);
        var seen = string.Join('\n', logger.Messages.Concat(pipeline.AuditEntries.Select(e => e.DetailsJson ?? string.Empty)));
        foreach (var token in tokens)
        {
            seen.ShouldNotContain(token);
        }

        for (var i = 0; i < 10; i++)
        {
            seen.ShouldNotContain((Base + i).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void OracleTextKey_NotEligible()
    {
        TableMetadata Table(string keyType, params string[] pk) => new()
        {
            Identifier = new TableIdentifier("crm", "dbo", "t"),
            Table = new Table { TableName = "t", SchemaName = "dbo", SourceName = "crm", SourceType = "Oracle", IsActive = true, DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "k", DataType = keyType }],
            PrimaryKeyColumns = pk,
        };

        IReadOnlyDictionary<TrinoSqlEngine.Governance.TableIdentity, TableMetadata> One(TableMetadata t) =>
            new Dictionary<TrinoSqlEngine.Governance.TableIdentity, TableMetadata> { [new TrinoSqlEngine.Governance.TableIdentity("dbo", "t", "crm")] = t };

        GovernedSqlRewriter.KeysetCandidateFor(One(Table("varchar2(20)", "k")), TargetSqlDialect.Oracle).ShouldBeNull();
        GovernedSqlRewriter.KeysetCandidateFor(One(Table("number", "k")), TargetSqlDialect.Oracle).ShouldNotBeNull();
        GovernedSqlRewriter.KeysetCandidateFor(One(Table("varchar", "k")), TargetSqlDialect.PostgreSql).ShouldNotBeNull();
        GovernedSqlRewriter.KeysetCandidateFor(One(Table("int", "k")), TargetSqlDialect.Ansi).ShouldBeNull();
        GovernedSqlRewriter.KeysetCandidateFor(One(Table("int")), TargetSqlDialect.PostgreSql).ShouldBeNull();
        GovernedSqlRewriter.KeysetCandidateFor(One(Table("int", "k", "k", "k", "k", "k", "k", "k", "k", "k")), TargetSqlDialect.PostgreSql).ShouldBeNull();
    }
}
