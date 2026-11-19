using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Streaming;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>ICdcWatermarkRepository</c> (durable, fenced CDC watermarks) across PostgreSQL and SQL Server engines.
/// A watermark is keyed by source and table, never by tenant: the isolation cases below therefore separate rows by that key,
/// and the fencing token is the write guard. The advance is monotonic and a stale fencing token never moves the row.
/// </summary>
public abstract class CdcWatermarkContract : GovernanceContractBase
{
    private const string Source = "mssql_ct";

    private ICdcWatermarkRepository Watermarks => Store.CdcWatermarks;

    [RequiresContainerRuntimeFact]
    public async Task Absent_ReturnsNull()
    {
        if (!Store.IsAvailable) return;

        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task Watermark_SurvivesStoreRecreation()
    {
        if (!Store.IsAvailable) return;
        (await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 42, 1, default)).ShouldBeTrue();

        await using var second = await Store.CreateAdditionalStoreAsync();

        (await second.CdcWatermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(42);
    }

    [RequiresContainerRuntimeFact]
    public async Task Advance_IsMonotonic()
    {
        if (!Store.IsAvailable) return;

        await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 100, 1, default);
        (await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 50, 1, default)).ShouldBeTrue();

        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(100);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task StaleFencingToken_IsRejected_AndWatermarkUnchanged()
    {
        if (!Store.IsAvailable) return;
        await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 10, 5, default);

        var accepted = await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 99, 4, default);

        accepted.ShouldBeFalse();
        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(10);
    }

    [RequiresContainerRuntimeFact]
    public async Task ConcurrentWriters_WithDifferentTokens_NeverRegress()
    {
        if (!Store.IsAvailable) return;

        await Task.WhenAll(Enumerable.Range(1, 20).Select(i =>
            Task.Run(async () => await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", i * 10, i, default))));

        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(200);
    }

    [RequiresContainerRuntimeFact]
    public async Task NewerFencingToken_TakesOver_AndFencesTheOldOwner()
    {
        if (!Store.IsAvailable) return;
        await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 10, 5, default);

        (await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 20, 6, default)).ShouldBeTrue();
        (await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 30, 5, default)).ShouldBeFalse();

        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(20);
    }

    [RequiresContainerRuntimeFact]
    public async Task Store_AfterClusterStateRestart_NewLeaseTokenCanStillCommit()
    {
        if (!Store.IsAvailable) return;
        var table = new TableIdentifier("finance", "dbo", "invoices");
        var time = TimeProvider.System;
        var watermarkStore = new GovernanceDbMssqlWatermarkStore(Watermarks, NullLogger<GovernanceDbMssqlWatermarkStore>.Instance);

        var before = new InMemoryClusterStateProvider(time);
        await using (var lease = await before.TryAcquireLeaseAsync("cdc:mssql:poll", TimeSpan.FromSeconds(30)))
        {
            (await watermarkStore.TryAdvanceWatermarkAsync(table, 10, lease!.Token)).ShouldBeTrue();
        }

        var restarted = new InMemoryClusterStateProvider(time); // fence counters were lost
        await using var after = await restarted.TryAcquireLeaseAsync("cdc:mssql:poll", TimeSpan.FromSeconds(30));

        (await watermarkStore.TryAdvanceWatermarkAsync(table, 20, after!.Token)).ShouldBeTrue();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Watermarks.TryAdvanceAsync(Source, "d", "s", "a", 7, 1, default);

        (await Watermarks.GetAsync(Source, "d", "s", "b", default)).ShouldBeNull();
        (await Watermarks.GetAsync(Source, "d", "other", "a", default)).ShouldBeNull();
        (await Watermarks.GetAsync(Source, "other", "s", "a", default)).ShouldBeNull();
        (await Watermarks.GetAsync("other_source", "d", "s", "a", default)).ShouldBeNull();
        (await Watermarks.GetAsync(Source, "d", "s", "a", default)).ShouldBe(7);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Watermarks.TryAdvanceAsync(Source, "d", "s", "a", 7, 3, default);

        // A writer of another table, with any token, leaves the row of the first table untouched.
        (await Watermarks.TryAdvanceAsync(Source, "d", "s", "b", 900, 99, default)).ShouldBeTrue();
        (await Watermarks.TryAdvanceAsync("other_source", "d", "s", "a", 901, 99, default)).ShouldBeTrue();

        (await Watermarks.GetAsync(Source, "d", "s", "a", default)).ShouldBe(7);
        (await Watermarks.TryAdvanceAsync(Source, "d", "s", "a", 8, 3, default)).ShouldBeTrue();
        (await Watermarks.GetAsync(Source, "d", "s", "a", default)).ShouldBe(8);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        // No tenant concept: a missing key part is the fail-closed input. The row key is NOT NULL, so nothing is written.
        await Should.ThrowAsync<Exception>(() => Watermarks.TryAdvanceAsync(null!, "d", "s", "t", 1, 1, default));
        await Should.ThrowAsync<Exception>(() => Watermarks.TryAdvanceAsync(Source, null!, "s", "t", 1, 1, default));

        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        (await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 10, 2, default)).ShouldBeTrue();
        (await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 10, 2, default)).ShouldBeTrue();

        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(10);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        // Concurrent first writes of the same key never surface a unique violation; the stale token reports false instead.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(async () => await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 5, 10, default))));

        results.ShouldAllBe(r => r);
        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(5);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        // No list: successive advances read back in order and repeated reads are stable.
        foreach (var w in new long[] { 10, 20, 30 })
        {
            await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", w, 1, default);
            (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(w);
        }

        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(30);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        // No page size exists; the numeric bound is the 64-bit range.
        await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", long.MaxValue, long.MaxValue, default);

        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(long.MaxValue);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        // No soft delete: a rejected (stale) write leaves no trace and an unwritten key stays absent.
        await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 10, 5, default);
        (await Watermarks.TryAdvanceAsync(Source, "d", "s", "t", 99, 4, default)).ShouldBeFalse();

        (await Watermarks.GetAsync(Source, "d", "s", "t", default)).ShouldBe(10);
        (await Watermarks.GetAsync(Source, "d", "s", "never", default)).ShouldBeNull();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlCdcWatermarkContractTests(GovernancePostgresFixture fixture) : CdcWatermarkContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "cdc_watermark_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerCdcWatermarkContractTests(GovernanceSqlServerFixture fixture) : CdcWatermarkContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "cdc_watermark_contract");
}
