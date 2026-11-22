using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.Governance.Contract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>Verifies multi-node governance cache coherence across PostgreSQL and SQL Server engines.</summary>
[Trait("Category", "Security")]
public abstract class CacheCoherenceClusterTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Sid Actor = new("S-1-5-21-1001");

    protected GovernanceTestStore Store { get; private set; } = null!;

    protected abstract Task<GovernanceTestStore> CreateStoreAsync();

    public async Task InitializeAsync() => Store = await CreateStoreAsync();

    public async Task DisposeAsync()
    {
        if (Store is not null)
        {
            await Store.DisposeAsync();
        }
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Second_node_sees_tightening_within_bound()
    {
        if (!Store.IsAvailable)
        {
            return;
        }

        var tableIdent = new TableIdentifier("sales", "public", $"orders_cluster_{Guid.NewGuid():N}");
        var storedMeta = await Store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableIdent,
            ReplaceColumns = true,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = tableIdent.Domain,
                SchemaName = tableIdent.Schema,
                TableName = tableIdent.TableName,
                Description = "Cluster cache coherence test table",
                Sensitivity = "NORMAL",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" },
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "salary", DataType = "decimal" }
            ]
        });

        var tableId = storedMeta.Table.Id;
        var domainRes = await Store.GovernanceDomains.UpsertAsync(
            new GovernanceDomain(
                Guid.NewGuid(),
                Store.TenantA,
                $"ClusterDomain-{Guid.NewGuid():N}",
                null,
                null,
                Actor,
                ProtectionClass.Internal,
                CatalogVisibilityMode.Open,
                IsSystemUnassigned: false,
                Version: 0,
                T0,
                T0),
            expectedVersion: 0,
            Actor);
        domainRes.Status.ShouldBe(GovernanceDomainWriteStatus.Created);

        await Store.GovernanceDomains.AssignTablesAsync(Store.TenantA, domainRes.Domain!.Id, [tableId], Actor);

        await using var node2 = await Store.CreateAdditionalStoreAsync();
        var sp1 = Store.Services.GetRequiredService<IProtectionSnapshotProvider>();
        var sp2 = node2.Services.GetRequiredService<IProtectionSnapshotProvider>();
        var cacheSettings = node2.Services.GetRequiredService<IOptions<GovernanceCacheSettings>>().Value;

        var initial1 = await sp1.GetAsync(Store.TenantA, tableIdent);
        var initial2 = await sp2.GetAsync(Store.TenantA, tableIdent);
        initial1.TableClass.ShouldBe(ProtectionClass.Internal);
        initial2.TableClass.ShouldBe(ProtectionClass.Internal);

        // Tighten protection on Node 1 without direct invalidation of Node 2.
        await Store.GovernanceDomains.SetProtectionAsync(
            Store.TenantA,
            tableId,
            "salary",
            new ObjectProtection(ProtectionClass.Secret, "PII_SALARY"),
            Actor);

        var immediate1 = await sp1.GetAsync(Store.TenantA, tableIdent);
        immediate1.TableClass.ShouldBe(ProtectionClass.Secret);
        immediate1.Columns["salary"].ShouldBe(new ColumnProtection(ProtectionClass.Secret, "PII_SALARY"));

        var deadline = TimeSpan.FromSeconds(cacheSettings.EpochPollSeconds + 3);
        EffectiveProtection? observed2 = null;
        await WaitHelper.WaitUntil(
            async () =>
            {
                var snap = await sp2.GetAsync(Store.TenantA, tableIdent);
                if (snap.TableClass != ProtectionClass.Secret)
                {
                    return false;
                }

                observed2 = snap;
                return true;
            },
            deadline,
            because: $"Node 2 did not observe protection tightening within {deadline.TotalSeconds:F1}s.");

        observed2.ShouldNotBeNull();
        observed2.TableClass.ShouldBe(ProtectionClass.Secret);
        observed2.Columns["salary"].ShouldBe(new ColumnProtection(ProtectionClass.Secret, "PII_SALARY"));

        // Other tenants remain isolated and unassigned (fail closed).
        var tenantBSnap = await sp2.GetAsync(Store.TenantB, tableIdent);
        tenantBSnap.TableClass.ShouldBe(ProtectionClass.Confidential);
        tenantBSnap.Visibility.ShouldBe(CatalogVisibilityMode.OnRequest);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlCacheCoherenceClusterTests(GovernancePostgresFixture fixture) : CacheCoherenceClusterTests
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "cache_coherence_cluster");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerCacheCoherenceClusterTests(GovernanceSqlServerFixture fixture) : CacheCoherenceClusterTests
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "cache_coherence_cluster");
}
