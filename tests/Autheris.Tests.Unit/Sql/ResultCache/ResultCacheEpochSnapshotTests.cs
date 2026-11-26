namespace Autheris.Tests.Unit.Sql.ResultCache;

using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Sql.ResultCache;
using Autheris.Domain.Common;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class ResultCacheEpochSnapshotTests
{
    private static readonly IReadOnlyList<TableIdentifier> Tables = [CacheHarness.Orders];

    private static ResultCacheEpochSnapshot Snapshot(long gov = 1, long table = 1, long generation = 1, long cluster = 0) =>
        new(gov, new Dictionary<TableIdentifier, long> { [CacheHarness.Orders] = table }, generation, cluster);

    [Fact]
    public async Task Capture_reads_every_component()
    {
        var store = new MutableEpochStore { Epoch = 7 };
        using var governance = new GovernanceEpochProvider(store, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }));
        var epochs = new FakeEpochService { Generation = 5, Cluster = 0 };

        var snapshot = await ResultCacheEpochSnapshot.TryCaptureAsync(governance, epochs, CacheHarness.Tenant, Tables, default);

        snapshot.ShouldNotBeNull();
        snapshot.GovernanceEpoch.ShouldBe(7);
        snapshot.Generation.ShouldBe(5);
        snapshot.ClusterSequence.ShouldBe(0);
        snapshot.TableEpochs[CacheHarness.Orders].ShouldBe(1);
    }

    [Fact]
    public async Task Capture_without_a_source_is_unavailable()
    {
        var snapshot = await ResultCacheEpochSnapshot.TryCaptureAsync(null, new FakeEpochService(), CacheHarness.Tenant, Tables, default);

        snapshot.ShouldBeNull();
    }

    [Fact]
    public void Equal_stamps_are_valid_and_every_difference_is_not()
    {
        var epochs = new FakeEpochService();

        ResultCacheEpochSnapshot.FindInvalidation(Snapshot(), Snapshot(), epochs, Tables).ShouldBeNull();
        ResultCacheEpochSnapshot.FindInvalidation(Snapshot(), Snapshot(gov: 2), epochs, Tables)
            .ShouldBe(WebSqlResultCacheInvalidationReasons.GovernanceEpoch);
        ResultCacheEpochSnapshot.FindInvalidation(Snapshot(), Snapshot(table: 2), epochs, Tables)
            .ShouldBe(WebSqlResultCacheInvalidationReasons.TableEpoch);
    }

    [Fact]
    public void An_invalidation_of_every_table_invalidates()
    {
        var epochs = new FakeEpochService { Invalidated = new HashSet<string> { "*" } };

        ResultCacheEpochSnapshot.FindInvalidation(Snapshot(), Snapshot(generation: 2), epochs, Tables)
            .ShouldBe(WebSqlResultCacheInvalidationReasons.TableEpoch);
    }

    [Fact]
    public void Missed_cluster_announcements_invalidate()
    {
        var epochs = new FakeEpochService();

        ResultCacheEpochSnapshot.FindInvalidation(Snapshot(), Snapshot(generation: 2, cluster: 1), epochs, Tables).ShouldBeNull();
        ResultCacheEpochSnapshot.FindInvalidation(Snapshot(), Snapshot(generation: 2, cluster: 2), epochs, Tables)
            .ShouldBe(WebSqlResultCacheInvalidationReasons.TableEpoch);
    }
}
