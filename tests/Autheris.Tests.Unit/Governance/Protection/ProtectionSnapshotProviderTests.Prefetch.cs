using Autheris.Application.Catalog;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Protection;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Protection;

/// <summary>
/// <see cref="ProtectionSnapshotProvider.ResolveManyAsync"/> resolves the snapshots of a decision batch set-based from the batch's own
/// metadata. Each snapshot must equal the per-table one, must never be cached (it was built from caller-supplied metadata), and tables it
/// cannot answer safely are left to the per-table path.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class ProtectionSnapshotProviderTests
{
    [Fact]
    public async Task ResolveMany_equals_the_per_table_snapshot_without_metadata_reads()
    {
        var (sut, metaRepo, domainRepo, meta, disposables) = await CreateBatchSutAsync();
        using var _ = disposables;
        var (reference, refDisposables) = CreateProvider(metaRepo, domainRepo, new LocalChangeNotifier(), new FakeTimeProvider(T0));
        using var __ = refDisposables;

        var batch = await sut.ResolveManyAsync(TenantA, [meta]);
        metaRepo.ReadCount.ShouldBe(0);

        var perTable = await reference.GetAsync(TenantA, meta.Identifier);
        var resolved = batch[meta.Identifier];
        resolved.TableClass.ShouldBe(perTable.TableClass);
        resolved.DeclaredTableClass.ShouldBe(perTable.DeclaredTableClass);
        resolved.TableMaskCategory.ShouldBe(perTable.TableMaskCategory);
        resolved.Visibility.ShouldBe(perTable.Visibility);
        resolved.Container.ShouldBe(perTable.Container);
        resolved.OwnerGroupSid.ShouldBe(perTable.OwnerGroupSid);
        resolved.Columns.Keys.OrderBy(static k => k).ShouldBe(perTable.Columns.Keys.OrderBy(static k => k));
        foreach (var (column, protection) in perTable.Columns)
        {
            resolved.Columns[column].ShouldBe(protection);
        }

        resolved.TableClass.ShouldBe(ProtectionClass.Secret);
    }

    [Fact]
    public async Task ResolveMany_does_not_cache_its_snapshots()
    {
        var (sut, metaRepo, _, meta, disposables) = await CreateBatchSutAsync();
        using var _ = disposables;

        await sut.ResolveManyAsync(TenantA, [meta]);
        await sut.GetAsync(TenantA, meta.Identifier);

        metaRepo.ReadCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveMany_returns_a_cached_snapshot_as_GetAsync_does()
    {
        var (sut, _, domainRepo, meta, disposables) = await CreateBatchSutAsync();
        using var _ = disposables;
        var cached = await sut.GetAsync(TenantA, meta.Identifier);
        var reads = domainRepo.ProtectionReadCount;

        var batch = await sut.ResolveManyAsync(TenantA, [meta]);

        batch[meta.Identifier].ShouldBeSameAs(cached);
        domainRepo.ProtectionReadCount.ShouldBe(reads);
    }

    [Fact]
    public async Task ResolveMany_leaves_inactive_tables_to_the_per_table_path()
    {
        var (sut, _, _, meta, disposables) = await CreateBatchSutAsync();
        using var _ = disposables;
        var active = MakeTable(Guid.NewGuid(), new TableIdentifier("hr", "public", "old"), ["id"]);
        var inactive = new TableMetadata
        {
            Identifier = active.Identifier,
            Table = new Table { Id = active.Table.Id, SchemaName = "public", TableName = "old", IsActive = false },
            Columns = active.Columns
        };

        var batch = await sut.ResolveManyAsync(TenantA, [meta, inactive]);

        batch.ContainsKey(meta.Identifier).ShouldBeTrue();
        batch.ContainsKey(inactive.Identifier).ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveMany_failure_propagates_and_the_per_table_read_still_fails_closed()
    {
        var (sut, _, domainRepo, meta, disposables) = await CreateBatchSutAsync();
        using var _ = disposables;
        domainRepo.DomainFailure = new InvalidOperationException("store down");

        await Should.ThrowAsync<InvalidOperationException>(async () => await sut.ResolveManyAsync(TenantA, [meta]));
        await Should.ThrowAsync<InvalidOperationException>(async () => await sut.GetAsync(TenantA, meta.Identifier));
    }

    [Fact]
    public async Task Batch_prefetch_answers_only_its_tenant_its_tables_and_within_max_age()
    {
        var (sut, _, _, meta, disposables) = await CreateBatchSutAsync();
        using var scope = disposables;
        var readAt = DateTimeOffset.UtcNow;
        var prefetch = await ProtectionBatchPrefetch.LoadAsync(sut, TenantA, [meta], readAt, CancellationToken.None);

        prefetch.TryGet(TenantA, meta.Identifier, readAt, out var hit).ShouldBeTrue();
        hit.TableClass.ShouldBe(ProtectionClass.Secret);
        prefetch.TryGet(TenantB, meta.Identifier, readAt, out _).ShouldBeFalse();
        prefetch.TryGet(TenantA, new TableIdentifier("hr", "public", "other"), readAt, out _).ShouldBeFalse();
        prefetch.TryGet(TenantA, meta.Identifier, readAt + ConsentBatchPrefetch.MaxAge + TimeSpan.FromSeconds(1), out _).ShouldBeFalse();
        prefetch.TryGet(TenantA, meta.Identifier, readAt - TimeSpan.FromSeconds(1), out _).ShouldBeFalse();
    }

    private static async Task<(ProtectionSnapshotProvider Sut, FakeTableMetadataRepository MetaRepo, FakeGovernanceDomainRepository DomainRepo,
        TableMetadata Meta, CompositeDisposable Disposables)> CreateBatchSutAsync()
    {
        var time = new FakeTimeProvider(T0);
        var notifier = new LocalChangeNotifier();
        var tableId = Guid.NewGuid();
        var meta = MakeTable(tableId, new TableIdentifier("hr", "public", "employees"), ["id", "salary"]);
        var metaRepo = new FakeTableMetadataRepository(meta);
        var domainRepo = new FakeGovernanceDomainRepository(notifier, time);
        domainRepo.SetContainer(TenantA, tableId, new ContainerDefaults(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), Actor, ProtectionClass.Internal, CatalogVisibilityMode.Open, IsValid: true));
        await domainRepo.SetProtectionAsync(TenantA, tableId, "salary", new ObjectProtection(ProtectionClass.Secret, "PII_SALARY"), Actor);

        var (sut, disposables) = CreateProvider(metaRepo, domainRepo, notifier, time);
        return (sut, metaRepo, domainRepo, meta, disposables);
    }

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
