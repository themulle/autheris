namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#112 T-3.2: the <see cref="ISemanticManifestStore"/> contract on real PostgreSql. Needs Docker; without it each test returns early.</summary>
public sealed class SemanticManifestPostgreSqlContractTests : IAsyncLifetime
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
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

    private static StoredSemanticManifest Sample(TenantId tenant, int n = 1) =>
        new(tenant, $"v{n}", new string((char)('a' + (n % 6)), 64), 1, 2, 3, $"{{\"n\":{n}}}", "admin", new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task SaveAndGet_RoundTrips_AndReplaces()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        ISemanticManifestStore store = repository;

        (await store.GetAsync(TenantA, default)).ShouldBeNull();
        await store.SaveAsync(TenantA, Sample(TenantA, 1), default);
        (await store.GetAsync(TenantA, default)).ShouldBe(Sample(TenantA, 1));

        await store.SaveAsync(TenantA, Sample(TenantA, 2), default);
        (await store.GetAsync(TenantA, default)).ShouldBe(Sample(TenantA, 2));
    }

    [Fact]
    public async Task Tenants_AreIsolated()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        ISemanticManifestStore store = repository;

        await store.SaveAsync(TenantA, Sample(TenantA), default);

        (await store.GetAsync(TenantB, default)).ShouldBeNull();
    }

    [Fact]
    public async Task ConcurrentSaves_LeaveExactlyOneConsistentRow()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        ISemanticManifestStore store = repository;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.SaveAsync(TenantB, Sample(TenantB, i), default)));

        var loaded = await store.GetAsync(TenantB, default);
        loaded.ShouldNotBeNull();
        loaded.ShouldBe(Sample(TenantB, int.Parse(loaded.Version[1..])));
    }
}
