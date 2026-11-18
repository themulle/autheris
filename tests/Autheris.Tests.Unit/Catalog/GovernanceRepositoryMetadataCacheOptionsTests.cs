namespace Autheris.Tests.Unit.Catalog;

using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Issue #678: <c>Gateway:Catalog:Cache</c> reaches the repository metadata cache, and a cached entry is never served without a readable epoch.</summary>
public sealed class GovernanceRepositoryMetadataCacheOptionsTests
{
    private static GovernanceRepository Repo(CatalogCacheOptions? cache)
    {
        var gateway = new GatewayOptions { Catalog = new DataCatalogOptions { Cache = cache ?? new CatalogCacheOptions() } };
        var dialect = new PostgreSqlGovernanceDialect(() => throw new InvalidOperationException("no database in this test"));
        return new GovernanceRepository(dialect, options: Options.Create(gateway));
    }

    private static TableMetadata Meta(TableIdentifier id) => new()
    {
        Table = new Table { Id = Guid.NewGuid(), SourceName = "ds", SchemaName = id.Schema, TableName = id.TableName },
        Identifier = id,
        Columns = []
    };

    [Fact]
    public void UsesConfiguredMaxEntriesAndTtl()
    {
        var repo = Repo(new CatalogCacheOptions { MetadataMaxEntries = 1_000, MetadataTtlSeconds = 7 });

        repo.MetadataCacheTtlTicks.ShouldBe(TimeSpan.FromSeconds(7).Ticks);
        for (var i = 0; i < 5_000; i++)
        {
            repo.MetadataCache.Set($"k{i}", (null, 0, 0));
        }

        repo.MetadataCache.Count.ShouldBeLessThanOrEqualTo(1_000);
    }

    [Fact]
    public void DefaultsMatchPreviousBehavior()
    {
        var repo = Repo(null);

        repo.MetadataCacheTtlTicks.ShouldBe(TimeSpan.FromSeconds(30).Ticks);
        for (var i = 0; i < 25_000; i++)
        {
            repo.MetadataCache.Set($"k{i}", (null, 0, 0));
        }

        repo.MetadataCache.Count.ShouldBeLessThanOrEqualTo(20_000);
        repo.MetadataCache.Count.ShouldBeGreaterThan(10_000);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CachedEntry_WithUnreadableEpoch_IsNeverServed()
    {
        var repo = Repo(null);
        var id = new TableIdentifier("sales", "dbo", "orders");
        var cached = Meta(id);
        repo.MetadataCache.Set(id.ToString().ToLowerInvariant(), (cached, TimeProvider.System.GetUtcNow().UtcTicks, 5));

        TableMetadata? served = null;
        try
        {
            served = await repo.GetTableMetadataAsync(id);
        }
        catch (Exception)
        {
            // Failing closed (no data source) is acceptable; serving the cached entry is not.
        }

        served.ShouldNotBeSameAs(cached);
    }
}
