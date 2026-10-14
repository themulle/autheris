namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-A-11: the durable, fenced CDC watermark contract on real PostgreSql. Needs Docker; without it each test returns early.</summary>
public sealed class CdcWatermarkPostgreSqlContractTests : IAsyncLifetime
{
    private const string Source = "mssql_ct";
    private PostgreSqlTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            _available = _db.IsAvailable;
        }
        catch (Exception ex)
        {
            _available = false;
            ContainerStartup.FailIfRequired(ex, "The PostgreSql test container");
        }
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private PostgreSqlGovernanceRepository NewRepository()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db!.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
        }), env);
    }

    [Fact]
    public async Task Absent_ReturnsNull()
    {
        if (!_available) return;
        await using var repository = NewRepository();

        (await repository.GetAsync(Source, "d", "s", "t", default)).ShouldBeNull();
    }

    [Fact]
    public async Task Watermark_SurvivesStoreRecreation()
    {
        if (!_available) return;
        await using (var first = NewRepository())
        {
            (await first.TryAdvanceAsync(Source, "d", "s", "t", 42, 1, default)).ShouldBeTrue();
        }

        await using var second = NewRepository();

        (await second.GetAsync(Source, "d", "s", "t", default)).ShouldBe(42);
    }

    [Fact]
    public async Task Advance_IsMonotonic()
    {
        if (!_available) return;
        await using var repository = NewRepository();

        await repository.TryAdvanceAsync(Source, "d", "s", "t", 100, 1, default);
        (await repository.TryAdvanceAsync(Source, "d", "s", "t", 50, 1, default)).ShouldBeTrue();

        (await repository.GetAsync(Source, "d", "s", "t", default)).ShouldBe(100);
    }

    [Fact]
    public async Task StaleFencingToken_IsRejected_AndWatermarkUnchanged()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        await repository.TryAdvanceAsync(Source, "d", "s", "t", 10, 5, default);

        var accepted = await repository.TryAdvanceAsync(Source, "d", "s", "t", 99, 4, default);

        accepted.ShouldBeFalse();
        (await repository.GetAsync(Source, "d", "s", "t", default)).ShouldBe(10);
    }

    [Fact]
    public async Task ConcurrentWriters_WithDifferentTokens_NeverRegress()
    {
        if (!_available) return;
        await using var repository = NewRepository();

        await Task.WhenAll(System.Linq.Enumerable.Range(1, 20).Select(i =>
            Task.Run(async () => await repository.TryAdvanceAsync(Source, "d", "s", "t", i * 10, i, default))));

        (await repository.GetAsync(Source, "d", "s", "t", default)).ShouldBe(200);
    }
}
