namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.KeySourceSqliteFixture;

/// <summary>
/// F-GOV-14 WI-5 (ST-19, SEC-10): the key set provider from the production wiring works next to the shared, size-limited
/// <see cref="IMemoryCache"/>: it caches in its own size-limited cache with an entry size, so neither cache throws.
/// </summary>
public sealed class VirtualFilterKeySetProviderWiringTests : IDisposable
{
    private readonly KeySourceSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ProductionWiring_TwoCalls_SecondIsCacheHit_EntryHasSize()
    {
        var keySource = _db.Options();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "Sqlite", ConnectionString = $"Data Source=vf_keys_wiring_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" },
            DataSources = keySource.DataSources,
            VirtualFilters = keySource.VirtualFilters
        };
        var services = new ServiceCollection();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        services.AddSingleton(env);
        services.AddSingleton(Options.Create(options));
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddGatewayInfrastructure(options);
        services.AddSingleton(_db.Catalog);
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMemoryCache>().ShouldBeOfType<MemoryCache>();
        var keys = provider.GetRequiredService<IVirtualFilterKeySetProvider>();
        keys.ShouldBeOfType<SqlVirtualFilterKeySetProvider>();

        var first = await keys.GetAsync(David(), [VirtualFilterKeyType.IntegerNumber], TenantA);
        var second = await keys.GetAsync(David(), [VirtualFilterKeyType.IntegerNumber], TenantA);

        first.CacheHit.ShouldBeFalse();
        second.CacheHit.ShouldBeTrue();
        second.Count.ShouldBe(3);
        provider.GetRequiredService<VirtualFilterKeySetCache>().CurrentEstimatedSize.ShouldBeGreaterThan(0);
        provider.GetRequiredService<ITableMetadataRepository>().ShouldBeSameAs(_db.Catalog);
        provider.GetRequiredService<IVirtualFilterFederationPlanner>().ShouldBeOfType<VirtualFilterFederationPlanner>();
    }
}
