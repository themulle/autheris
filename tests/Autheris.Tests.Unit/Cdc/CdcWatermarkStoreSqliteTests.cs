namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Streaming;
using Autheris.Tests.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>The durable governance-database watermark store behind <see cref="IMssqlWatermarkStore"/> (WP-ARC-A-11).</summary>
public sealed class CdcWatermarkStoreSqliteTests : IDisposable
{
    private static readonly TableIdentifier Table = new("finance", "dbo", "invoices");
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private (SqliteGovernanceRepository Repository, GovernanceDbMssqlWatermarkStore Store) NewStore()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source={System.IO.Path.Combine(_dir.Path, "gov.db")}",
                SeedDemoData = false,
                EnableOutboxProcessor = false
            }
        });
        var repository = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options, env);
        return (repository, new GovernanceDbMssqlWatermarkStore(repository, NullLogger<GovernanceDbMssqlWatermarkStore>.Instance));
    }

    [Fact]
    public async Task Absent_ReturnsNull()
    {
        var (repository, store) = NewStore();
        using (repository)
        {
            (await store.GetWatermarkAsync(Table)).ShouldBeNull();
            store.IsDurable.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Watermark_SurvivesStoreRecreation()
    {
        var (first, firstStore) = NewStore();
        using (first)
        {
            (await firstStore.TryAdvanceWatermarkAsync(Table, 42, 1)).ShouldBeTrue();
        }

        var (second, secondStore) = NewStore();
        using (second)
        {
            (await secondStore.GetWatermarkAsync(Table)).ShouldBe(42);
        }
    }

    [Fact]
    public async Task Advance_IsMonotonic()
    {
        var (repository, store) = NewStore();
        using (repository)
        {
            await store.TryAdvanceWatermarkAsync(Table, 100, 1);
            await store.TryAdvanceWatermarkAsync(Table, 50, 1);

            (await store.GetWatermarkAsync(Table)).ShouldBe(100);
        }
    }

    [Fact]
    public async Task StaleFencingToken_IsRejected_AndWatermarkUnchanged()
    {
        var (repository, store) = NewStore();
        using (repository)
        {
            await store.TryAdvanceWatermarkAsync(Table, 10, 5);

            (await store.TryAdvanceWatermarkAsync(Table, 99, 4)).ShouldBeFalse();

            (await store.GetWatermarkAsync(Table)).ShouldBe(10);
        }
    }

    [Fact]
    public async Task AfterClusterStateRestart_NewLeaseTokenCanStillCommit()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var (repository, store) = NewStore();
        using (repository)
        {
            var before = new Autheris.Application.State.InMemoryClusterStateProvider(time);
            await using (var lease = await before.TryAcquireLeaseAsync("cdc:mssql:poll", TimeSpan.FromSeconds(30)))
            {
                (await store.TryAdvanceWatermarkAsync(Table, 10, lease!.Token)).ShouldBeTrue();
            }

            time.Advance(TimeSpan.FromSeconds(5));
            var restarted = new Autheris.Application.State.InMemoryClusterStateProvider(time); // fence counters were lost
            await using var after = await restarted.TryAcquireLeaseAsync("cdc:mssql:poll", TimeSpan.FromSeconds(30));

            (await store.TryAdvanceWatermarkAsync(Table, 20, after!.Token)).ShouldBeTrue();
        }
    }
}
