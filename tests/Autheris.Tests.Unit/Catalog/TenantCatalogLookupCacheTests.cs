namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.State.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #678: the tenant snapshot cache is bounded and one tenant cannot evict the others.</summary>
public sealed class TenantCatalogLookupCacheTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private static ITableMetadataRepository Repository(out int[] loads)
    {
        var counter = new int[1];
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            counter[0]++;
            return Task.FromResult<IReadOnlyList<TableMetadata>>(
            [
                new TableMetadata { Table = new Table { SourceName = "ds", SchemaName = "dbo", TableName = "orders" }, Identifier = Orders, Columns = [] }
            ]);
        });
        loads = counter;
        return repo;
    }

    [Fact]
    public async Task BoundedAcrossTenants()
    {
        var sut = new TenantCatalogLookup(Repository(out var loads), maxEntries: 4);
        for (var i = 0; i < 10; i++)
        {
            (await sut.FindByIdentifierAsync(new TenantId($"t{i}"), Orders)).ShouldNotBeNull();
        }

        loads[0].ShouldBe(10);
        // t0 was evicted (capacity 4): a repeat call reloads instead of failing or growing the cache.
        (await sut.FindByIdentifierAsync(new TenantId("t0"), Orders)).ShouldNotBeNull();
        loads[0].ShouldBe(11);
        // t9 is still cached.
        await sut.FindByIdentifierAsync(new TenantId("t9"), Orders);
        loads[0].ShouldBe(11);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task OneTenantCannotEvictAll()
    {
        var sut = new TenantCatalogLookup(Repository(out var loads), maxEntries: 20);
        for (var i = 0; i < 5; i++)
        {
            await sut.FindByIdentifierAsync(new TenantId($"quiet{i}"), Orders);
        }

        for (var i = 0; i < 200; i++)
        {
            sut.Invalidate(new TenantId("noisy"));
            await sut.FindByIdentifierAsync(new TenantId("noisy"), Orders);
        }

        var before = loads[0];
        for (var i = 0; i < 5; i++)
        {
            await sut.FindByIdentifierAsync(new TenantId($"quiet{i}"), Orders);
        }

        loads[0].ShouldBe(before, "the quiet tenants' snapshots must survive another tenant's churn");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SharedCache_CapsEveryTenantAtHalfTheCapacity()
    {
        var cache = new TenantShareCappedCache<(string Tenant, int Id), int>(100, static k => k.Tenant);
        for (var i = 0; i < 10; i++)
        {
            cache.Set(("quiet", i), i);
        }

        for (var i = 0; i < 1_000; i++)
        {
            cache.Set(("noisy", i), i);
            for (var q = 0; q < 10; q++)
            {
                cache.TryGet(("quiet", q), out _); // the quiet tenant keeps using its entries
            }
        }

        cache.PerTenantCap.ShouldBe(50);
        for (var i = 0; i < 10; i++)
        {
            cache.TryGet(("quiet", i), out _).ShouldBeTrue();
        }

        var noisy = Enumerable.Range(0, 1_000).Count(i => cache.TryGet(("noisy", i), out _));
        noisy.ShouldBeLessThanOrEqualTo(50);
        cache.TryGet(("noisy", 999), out _).ShouldBeTrue();
        cache.Count.ShouldBeLessThanOrEqualTo(100);
    }
}
