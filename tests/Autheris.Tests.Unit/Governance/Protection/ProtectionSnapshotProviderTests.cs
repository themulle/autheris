using System.Collections.Concurrent;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Protection;

[Trait("Category", "Security")]
public sealed class ProtectionSnapshotProviderTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid Actor = new("S-1-5-21-1001");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Invalidated_on_class_change()
    {
        var time = new FakeTimeProvider(T0);
        var notifier = new LocalChangeNotifier();
        var domainId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var tableIdent = new TableIdentifier("hr", "public", "employees");
        var metaRepo = new FakeTableMetadataRepository(MakeTable(tableId, tableIdent, ["id", "salary"]));
        var domainRepo = new FakeGovernanceDomainRepository(notifier, time);
        domainRepo.SetContainer(TenantA, tableId, new ContainerDefaults(
            new ContainerRef(ContainerKind.Domain, domainId),
            Actor,
            ProtectionClass.Internal,
            CatalogVisibilityMode.Open,
            IsValid: true));
        await domainRepo.SetProtectionAsync(TenantA, tableId, "", new ObjectProtection(ProtectionClass.Internal), Actor);

        var catalogVersion = new FakeCatalogVersionProvider(new CatalogVersion(1, 1));
        using var epochProvider = new GovernanceEpochProvider(
            domainRepo,
            Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }),
            notifier,
            time);
        using var sut = new ProtectionSnapshotProvider(
            metaRepo,
            domainRepo,
            epochProvider,
            catalogVersion,
            Options.Create(new GovernanceCacheSettings { MaxTtlSeconds = 300, NegativeTtlSeconds = 60, EpochPollSeconds = 60 }),
            notifier,
            time);

        var first = await sut.GetAsync(TenantA, tableIdent);
        first.TableClass.ShouldBe(ProtectionClass.Internal);
        first.DeclaredTableClass.ShouldBe(ProtectionClass.Internal);
        first.Visibility.ShouldBe(CatalogVisibilityMode.Open);
        first.Columns["salary"].EffectiveClass.ShouldBe(ProtectionClass.Internal);

        // Repeated lookup within TTL stays cached.
        var cached = await sut.GetAsync(TenantA, tableIdent);
        cached.TableClass.ShouldBe(ProtectionClass.Internal);
        domainRepo.ProtectionReadCount.ShouldBe(1);

        // Tightening class triggers immediate invalidation via IGovernanceChangeNotifier without waiting for TTL or poll interval.
        await domainRepo.SetProtectionAsync(TenantA, tableId, "salary", new ObjectProtection(ProtectionClass.Secret, "PII_SALARY"), Actor);

        var updated = await sut.GetAsync(TenantA, tableIdent);
        updated.TableClass.ShouldBe(ProtectionClass.Secret);
        updated.DeclaredTableClass.ShouldBe(ProtectionClass.Internal);
        updated.Columns["salary"].ShouldBe(new ColumnProtection(ProtectionClass.Secret, "PII_SALARY"));
        domainRepo.ProtectionReadCount.ShouldBe(2);
    }

    [Fact]
    public async Task Store_failure_denies()
    {
        var time = new FakeTimeProvider(T0);
        var notifier = new LocalChangeNotifier();
        var tableId = Guid.NewGuid();
        var tableIdent = new TableIdentifier("fin", "dbo", "ledger");
        var metaRepo = new FakeTableMetadataRepository(MakeTable(tableId, tableIdent, ["amount"]));
        var domainRepo = new FakeGovernanceDomainRepository(notifier, time);
        domainRepo.SetContainer(TenantA, tableId, new ContainerDefaults(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            Actor,
            ProtectionClass.Internal,
            CatalogVisibilityMode.Open,
            IsValid: true));
        var catalogVersion = new FakeCatalogVersionProvider(new CatalogVersion(1, 1));

        using var epochProvider = new GovernanceEpochProvider(
            domainRepo,
            Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 2 }),
            notifier,
            time);
        using var sut = new ProtectionSnapshotProvider(
            metaRepo,
            domainRepo,
            epochProvider,
            catalogVersion,
            Options.Create(new GovernanceCacheSettings { MaxTtlSeconds = 300, NegativeTtlSeconds = 60, EpochPollSeconds = 2 }),
            notifier,
            time);

        // Warm cache for TenantA, fail closed when epoch store errors past poll window.
        (await sut.GetAsync(TenantA, tableIdent)).TableClass.ShouldBe(ProtectionClass.Internal);
        time.Advance(TimeSpan.FromSeconds(3));
        domainRepo.EpochFailure = new InvalidOperationException("epoch db unreachable");
        await Should.ThrowAsync<InvalidOperationException>(() => sut.GetAsync(TenantA, tableIdent).AsTask());
        domainRepo.EpochFailure = null;

        // Cold read failure on domain repository propagates instead of defaulting to Open/Public.
        domainRepo.DomainFailure = new InvalidOperationException("protection db failure");
        await Should.ThrowAsync<InvalidOperationException>(() => sut.GetAsync(TenantB, tableIdent).AsTask());
        domainRepo.DomainFailure = null;

        // Metadata repository failure propagates cleanly.
        metaRepo.Failure = new InvalidOperationException("catalog table read failure");
        await Should.ThrowAsync<InvalidOperationException>(() => sut.GetAsync(TenantB, tableIdent).AsTask());
        metaRepo.Failure = null;

        // Catalog version failure propagates cleanly.
        catalogVersion.Failure = new InvalidOperationException("catalog version unreachable");
        await Should.ThrowAsync<InvalidOperationException>(() => sut.GetAsync(TenantB, tableIdent).AsTask());

        // Invalid TenantId and invalid TableIdentifier fail closed.
        await Should.ThrowAsync<ArgumentException>(() => sut.GetAsync(default, tableIdent).AsTask());
        await Should.ThrowAsync<ArgumentException>(() => sut.GetAsync(TenantId.Platform, tableIdent).AsTask());
        await Should.ThrowAsync<ArgumentException>(() => sut.GetAsync(TenantA, new TableIdentifier("", "dbo", "ledger")).AsTask());
    }

    [Fact]
    public async Task Negative_result_ttl_bounded()
    {
        var time = new FakeTimeProvider(T0);
        var notifier = new LocalChangeNotifier();
        var tableId = Guid.NewGuid();
        var tableIdent = new TableIdentifier("hr", "public", "pending_table");
        var metaRepo = new FakeTableMetadataRepository();
        var domainRepo = new FakeGovernanceDomainRepository(notifier, time);
        var catalogVersion = new FakeCatalogVersionProvider(new CatalogVersion(1, 1));

        using var epochProvider = new GovernanceEpochProvider(
            domainRepo,
            Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }),
            notifier,
            time);
        using var sut = new ProtectionSnapshotProvider(
            metaRepo,
            domainRepo,
            epochProvider,
            catalogVersion,
            Options.Create(new GovernanceCacheSettings { MaxTtlSeconds = 300, NegativeTtlSeconds = 15, EpochPollSeconds = 60 }),
            notifier,
            time);

        // Missing/inactive table produces a fail-closed Confidential negative snapshot.
        var neg1 = await sut.GetAsync(TenantA, tableIdent);
        neg1.Container.ShouldBe(new ContainerRef(ContainerKind.Domain, Guid.Empty));
        neg1.TableClass.ShouldBe(ProtectionClass.Confidential);
        neg1.Visibility.ShouldBe(CatalogVisibilityMode.OnRequest);
        metaRepo.ReadCount.ShouldBe(1);

        // Before negative TTL expires, negative entry remains cached even if table is created out-of-band.
        metaRepo.Upsert(MakeTable(tableId, tableIdent, ["id"]));
        domainRepo.SetContainer(TenantA, tableId, new ContainerDefaults(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            Actor,
            ProtectionClass.Internal,
            CatalogVisibilityMode.Open,
            IsValid: true));
        time.Advance(TimeSpan.FromSeconds(14));
        var stillNeg = await sut.GetAsync(TenantA, tableIdent);
        stillNeg.TableClass.ShouldBe(ProtectionClass.Confidential);
        metaRepo.ReadCount.ShouldBe(1);

        // Once NegativeTtlSeconds (15s) has elapsed (well below MaxTtlSeconds = 300s), entry is evicted and reloaded.
        time.Advance(TimeSpan.FromSeconds(1));
        var reloaded = await sut.GetAsync(TenantA, tableIdent);
        reloaded.TableClass.ShouldBe(ProtectionClass.Internal);
        reloaded.Visibility.ShouldBe(CatalogVisibilityMode.Open);
        metaRepo.ReadCount.ShouldBe(2);

        // Positive entry stays cached beyond NegativeTtlSeconds (e.g. +120s) and expires cleanly at MaxTtlSeconds (300s).
        time.Advance(TimeSpan.FromSeconds(120));
        (await sut.GetAsync(TenantA, tableIdent)).TableClass.ShouldBe(ProtectionClass.Internal);
        metaRepo.ReadCount.ShouldBe(2);

        time.Advance(TimeSpan.FromSeconds(180));
        (await sut.GetAsync(TenantA, tableIdent)).TableClass.ShouldBe(ProtectionClass.Internal);
        metaRepo.ReadCount.ShouldBe(3);
    }

    [Fact]
    public async Task Catalog_version_and_epoch_poll_invalidate_cached_snapshots()
    {
        var time = new FakeTimeProvider(T0);
        var notifier = new LocalChangeNotifier();
        var domainId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var tableIdent = new TableIdentifier("sales", "public", "orders");
        var metaRepo = new FakeTableMetadataRepository(MakeTable(tableId, tableIdent, ["id", "total"]));
        var domainRepo = new FakeGovernanceDomainRepository(notifier, time);
        domainRepo.SetContainer(TenantA, tableId, new ContainerDefaults(
            new ContainerRef(ContainerKind.Domain, domainId),
            Actor,
            ProtectionClass.Internal,
            CatalogVisibilityMode.Open,
            IsValid: true));
        var catalogVersion = new FakeCatalogVersionProvider(new CatalogVersion(1, 1));

        using var epochProvider = new GovernanceEpochProvider(
            domainRepo,
            Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 2 }),
            changeNotifier: null,
            time);
        using var sut = new ProtectionSnapshotProvider(
            metaRepo,
            domainRepo,
            epochProvider,
            catalogVersion,
            Options.Create(new GovernanceCacheSettings { MaxTtlSeconds = 300, NegativeTtlSeconds = 60, EpochPollSeconds = 2 }),
            changeNotifier: null,
            time);

        (await sut.GetAsync(TenantA, tableIdent)).TableClass.ShouldBe(ProtectionClass.Internal);
        domainRepo.ProtectionReadCount.ShouldBe(1);

        // Catalog version advance forces a fresh lookup even without epoch bump.
        catalogVersion.Current = new CatalogVersion(2, 1);
        (await sut.GetAsync(TenantA, tableIdent)).TableClass.ShouldBe(ProtectionClass.Internal);
        domainRepo.ProtectionReadCount.ShouldBe(2);

        // Out-of-band DB mutation + epoch advance (no Redis/local pub-sub) is detected once EpochPollSeconds elapses.
        domainRepo.SetContainer(TenantA, tableId, new ContainerDefaults(
            new ContainerRef(ContainerKind.Domain, domainId),
            Actor,
            ProtectionClass.Confidential,
            CatalogVisibilityMode.OnRequest,
            IsValid: true));
        await domainRepo.BumpEpochAsync(TenantA);
        time.Advance(TimeSpan.FromSeconds(2));

        var observed = await sut.GetAsync(TenantA, tableIdent);
        observed.TableClass.ShouldBe(ProtectionClass.Confidential);
        observed.Visibility.ShouldBe(CatalogVisibilityMode.OnRequest);
    }

    private static TableMetadata MakeTable(Guid tableId, TableIdentifier ident, IReadOnlyList<string> columns) =>
        new()
        {
            Identifier = ident,
            Table = new Table
            {
                Id = tableId,
                SchemaName = ident.Schema,
                TableName = ident.TableName,
                IsActive = true
            },
            Columns = columns.Select(c => new TableColumn { TableId = tableId, ColumnName = c }).ToList()
        };

    private sealed class FakeCatalogVersionProvider(CatalogVersion initial) : ICatalogVersionProvider
    {
        public CatalogVersion Current { get; set; } = initial;
        public Exception? Failure { get; set; }

        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) =>
            Failure is { } ex
                ? ValueTask.FromException<CatalogVersion>(ex)
                : ValueTask.FromResult(Current);

        public Task<string> GetVersionAsync(CancellationToken ct = default) =>
            Failure is { } ex
                ? Task.FromException<string>(ex)
                : Task.FromResult(Current.Formatted);
    }

    private sealed class LocalChangeNotifier : IGovernanceChangeNotifier
    {
        private readonly ConcurrentDictionary<Guid, Action<GovernanceChangeEvent>> _handlers = new();

        public Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default)
        {
            foreach (var h in _handlers.Values)
            {
                h(change);
            }

            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Action<GovernanceChangeEvent> handler)
        {
            var id = Guid.NewGuid();
            _handlers.TryAdd(id, handler);
            return new Token(() => _handlers.TryRemove(id, out _));
        }

        private sealed class Token(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class FakeTableMetadataRepository : ITableMetadataRepository
    {
        private readonly ConcurrentDictionary<TableIdentifier, TableMetadata> _tables = new();
        public Exception? Failure { get; set; }
        public int ReadCount { get; private set; }

        public FakeTableMetadataRepository(params TableMetadata[] initial)
        {
            foreach (var item in initial)
            {
                _tables[item.Identifier] = item;
            }
        }

        public void Upsert(TableMetadata meta) => _tables[meta.Identifier] = meta;

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            ReadCount++;
            if (Failure is { } ex)
            {
                return Task.FromException<TableMetadata?>(ex);
            }

            return Task.FromResult(_tables.TryGetValue(table, out var meta) ? meta : null);
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>(_tables.Values.ToList());

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            _tables[metadata.Identifier] = metadata;
            return Task.FromResult(metadata);
        }

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier tableId,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class FakeGovernanceDomainRepository(IGovernanceChangeNotifier notifier, TimeProvider time)
        : IGovernanceDomainRepository, IGovernanceEpochStore
    {
        private readonly ConcurrentDictionary<(string Tenant, Guid TableId), ContainerDefaults> _containers = new();
        private readonly ConcurrentDictionary<(string Tenant, Guid TableId), (ObjectProtection? Table, Dictionary<string, ObjectProtection> Columns)> _protection = new();
        private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.Ordinal);

        public Exception? EpochFailure { get; set; }
        public Exception? DomainFailure { get; set; }
        public int ProtectionReadCount { get; private set; }

        public void SetContainer(TenantId tenant, Guid tableId, ContainerDefaults defaults) =>
            _containers[(tenant.Value, tableId)] = defaults;

        public Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default) =>
            EpochFailure is { } ex
                ? Task.FromException<long>(ex)
                : Task.FromResult(_epochs.GetValueOrDefault(tenant.Value, 0L));

        public Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult(_epochs.AddOrUpdate(tenant.Value, 1, static (_, e) => e + 1));

        public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(
            TenantId tenant,
            Guid tableId,
            CancellationToken ct = default)
        {
            ProtectionReadCount++;
            if (DomainFailure is { } ex)
            {
                return Task.FromException<(ObjectProtection?, IReadOnlyDictionary<string, ObjectProtection>)>(ex);
            }

            if (_protection.TryGetValue((tenant.Value, tableId), out var entry))
            {
                return Task.FromResult<(ObjectProtection?, IReadOnlyDictionary<string, ObjectProtection>)>(
                    (entry.Table, entry.Columns.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)));
            }

            return Task.FromResult<(ObjectProtection?, IReadOnlyDictionary<string, ObjectProtection>)>(
                (null, new Dictionary<string, ObjectProtection>(StringComparer.OrdinalIgnoreCase)));
        }

        public async Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default)
        {
            var entry = _protection.GetOrAdd((tenant.Value, tableId), static _ => (null, new Dictionary<string, ObjectProtection>(StringComparer.OrdinalIgnoreCase)));
            if (column.Length == 0)
            {
                _protection[(tenant.Value, tableId)] = (protection, entry.Columns);
            }
            else
            {
                entry.Columns[column] = protection;
            }

            var epoch = await BumpEpochAsync(tenant, ct).ConfigureAwait(false);
            await notifier.NotifyAsync(new GovernanceChangeEvent(tenant.Value, epoch, tableId, time.GetUtcNow()), ct).ConfigureAwait(false);
        }

        public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default)
        {
            if (DomainFailure is { } ex)
            {
                return Task.FromException<ContainerDefaults>(ex);
            }

            return Task.FromResult(_containers.TryGetValue((tenant.Value, tableId), out var d) ? d : ContainerDefaults.Unassigned);
        }

        public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetTableProtectionAsync(TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
