using Autheris.Application.Catalog;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Protection;

/// <summary>
/// <see cref="ProtectionSnapshotProvider.PrefetchAsync"/> loads the uncached snapshots of a decision batch set-based. It must cache exactly
/// what the per-table path would resolve, under the same key, and nothing when an invalidation raced the load or a read failed.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class ProtectionSnapshotProviderTests
{
    [Fact]
    public async Task Prefetch_caches_the_snapshot_the_per_table_path_resolves()
    {
        var (sut, metaRepo, domainRepo, ident, _, disposables) = await CreatePrefetchSutAsync();
        using var _ = disposables;
        var (reference, refDisposables) = CreateReferenceSut(metaRepo, domainRepo);
        using var __ = refDisposables;

        await sut.PrefetchAsync(TenantA, [ident]);
        var metadataReads = metaRepo.ReadCount;
        var protectionReads = domainRepo.ProtectionReadCount;

        var prefetched = await sut.GetAsync(TenantA, ident);
        var perTable = await reference.GetAsync(TenantA, ident);

        metaRepo.ReadCount.ShouldBe(metadataReads + 1); // only the reference provider read
        domainRepo.ProtectionReadCount.ShouldBe(protectionReads + 1);
        prefetched.TableClass.ShouldBe(perTable.TableClass);
        prefetched.DeclaredTableClass.ShouldBe(perTable.DeclaredTableClass);
        prefetched.Visibility.ShouldBe(perTable.Visibility);
        prefetched.Container.ShouldBe(perTable.Container);
        prefetched.Columns.Keys.OrderBy(static k => k).ShouldBe(perTable.Columns.Keys.OrderBy(static k => k));
        foreach (var (column, protection) in perTable.Columns)
        {
            prefetched.Columns[column].ShouldBe(protection);
        }

        prefetched.Columns["salary"].ShouldBe(new ColumnProtection(ProtectionClass.Secret, "PII_SALARY"));
    }

    [Fact]
    public async Task Prefetch_caches_nothing_when_an_invalidation_races_the_load()
    {
        var (sut, metaRepo, _, ident, _, disposables) = await CreatePrefetchSutAsync();
        using var _ = disposables;
        metaRepo.OnRead = () => sut.InvalidateTenant(TenantA.Value);

        await sut.PrefetchAsync(TenantA, [ident]);
        metaRepo.OnRead = null;
        var reads = metaRepo.ReadCount;

        await sut.GetAsync(TenantA, ident);

        metaRepo.ReadCount.ShouldBe(reads + 1);
    }

    [Fact]
    public async Task Prefetch_failure_caches_nothing_and_the_per_table_read_still_fails_closed()
    {
        var (sut, _, domainRepo, ident, _, disposables) = await CreatePrefetchSutAsync();
        using var _ = disposables;
        domainRepo.DomainFailure = new InvalidOperationException("store down");

        await Should.ThrowAsync<InvalidOperationException>(async () => await sut.PrefetchAsync(TenantA, [ident]));
        await Should.ThrowAsync<InvalidOperationException>(async () => await sut.GetAsync(TenantA, ident));
    }

    [Fact]
    public async Task Prefetch_of_an_unknown_table_caches_the_negative_default()
    {
        var (sut, metaRepo, _, _, _, disposables) = await CreatePrefetchSutAsync();
        using var _ = disposables;
        var unknown = new TableIdentifier("hr", "public", "missing");

        await sut.PrefetchAsync(TenantA, [unknown]);
        var reads = metaRepo.ReadCount;
        var snapshot = await sut.GetAsync(TenantA, unknown);

        metaRepo.ReadCount.ShouldBe(reads);
        snapshot.TableClass.ShouldBe(ProtectionClass.Confidential);
        snapshot.Visibility.ShouldBe(CatalogVisibilityMode.OnRequest);
    }

    [Fact]
    public async Task Prefetch_for_one_tenant_is_not_served_to_another()
    {
        var (sut, metaRepo, _, ident, _, disposables) = await CreatePrefetchSutAsync();
        using var _ = disposables;

        await sut.PrefetchAsync(TenantA, [ident]);
        var reads = metaRepo.ReadCount;
        await sut.GetAsync(TenantB, ident);

        metaRepo.ReadCount.ShouldBe(reads + 1);
    }

    private static async Task<(ProtectionSnapshotProvider Sut, FakeTableMetadataRepository MetaRepo, FakeGovernanceDomainRepository DomainRepo,
        TableIdentifier Ident, Guid TableId, CompositeDisposable Disposables)> CreatePrefetchSutAsync()
    {
        var time = new FakeTimeProvider(T0);
        var notifier = new LocalChangeNotifier();
        var tableId = Guid.NewGuid();
        var ident = new TableIdentifier("hr", "public", "employees");
        var metaRepo = new FakeTableMetadataRepository(MakeTable(tableId, ident, ["id", "salary"]));
        var domainRepo = new FakeGovernanceDomainRepository(notifier, time);
        domainRepo.SetContainer(TenantA, tableId, new ContainerDefaults(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), Actor, ProtectionClass.Internal, CatalogVisibilityMode.Open, IsValid: true));
        await domainRepo.SetProtectionAsync(TenantA, tableId, "salary", new ObjectProtection(ProtectionClass.Secret, "PII_SALARY"), Actor);

        var (sut, disposables) = CreateProvider(metaRepo, domainRepo, notifier, time);
        return (sut, metaRepo, domainRepo, ident, tableId, disposables);
    }

    private static (ProtectionSnapshotProvider Sut, CompositeDisposable Disposables) CreateReferenceSut(
        FakeTableMetadataRepository metaRepo, FakeGovernanceDomainRepository domainRepo) =>
        CreateProvider(metaRepo, domainRepo, new LocalChangeNotifier(), new FakeTimeProvider(T0));

    private static (ProtectionSnapshotProvider Sut, CompositeDisposable Disposables) CreateProvider(
        FakeTableMetadataRepository metaRepo, FakeGovernanceDomainRepository domainRepo, LocalChangeNotifier notifier, FakeTimeProvider time)
    {
        var epochProvider = new GovernanceEpochProvider(
            domainRepo, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }), notifier, time);
        var sut = new ProtectionSnapshotProvider(
            metaRepo,
            domainRepo,
            epochProvider,
            new FakeCatalogVersionProvider(new CatalogVersion(1, 1)),
            Options.Create(new GovernanceCacheSettings { MaxTtlSeconds = 300, NegativeTtlSeconds = 60, EpochPollSeconds = 60 }),
            notifier,
            time);
        return (sut, new CompositeDisposable(sut, epochProvider));
    }

    private sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }
}
