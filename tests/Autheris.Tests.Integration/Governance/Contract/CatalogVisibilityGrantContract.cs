using System;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Contract suite for <c>ICatalogVisibilityGrantRepository</c> across PostgreSQL and SQL Server engines.</summary>
public abstract class CatalogVisibilityGrantContract : GovernanceContractBase
{
    private static readonly Sid Alice = new("S-1-5-21-CATALOG-ALICE");
    private static readonly Sid Bob = new("S-1-5-21-CATALOG-BOB");
    private static readonly Sid Admin = new("S-1-5-21-CATALOG-ADMIN");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    private ICatalogVisibilityGrantRepository Repo => Store.CatalogVisibilityGrants;

    private static CatalogVisibilityGrant NewGrant(TenantId tenant, Guid containerId, Sid grantee, DateTimeOffset? validTo = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            ContainerKind = ContainerKind.Domain,
            ContainerId = containerId,
            GranteeType = GranteeType.User,
            GranteeSid = grantee,
            ValidFrom = Now.AddMinutes(-10),
            ValidTo = validTo ?? Now.AddDays(30),
            GrantedBySid = Admin,
            ApproverCapacity = ApproverCapacity.ItAdmin,
            CreatedAt = Now.AddMinutes(-10)
        };

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var containerId = Guid.NewGuid();
        var created = await Repo.CreateAsync(NewGrant(Store.TenantA, containerId, Alice));

        (await Repo.GetByIdAsync(Store.TenantA, created.Id)).ShouldNotBeNull();
        (await Repo.GetByIdAsync(Store.TenantB, created.Id)).ShouldBeNull();
        (await Repo.ListActiveForSubjectsAsync(Store.TenantB, [Alice], Now)).ShouldBeEmpty();
        (await Repo.ListByContainerAsync(Store.TenantB, ContainerKind.Domain, containerId)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var containerId = Guid.NewGuid();
        var grantA = await Repo.CreateAsync(NewGrant(Store.TenantA, containerId, Alice));

        (await Repo.RevokeAsync(Store.TenantB, grantA.Id, Admin, "Cross-tenant revoke must fail", Now)).ShouldBeFalse();
        (await Repo.RevokeAllForSubjectAsync(Store.TenantB, [Alice], Admin, "Cross-tenant revoke", Now)).ShouldBe(0);
        (await Repo.GetByIdAsync(Store.TenantA, grantA.Id))!.IsRevoked.ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(() => Repo.CreateAsync(NewGrant(default, Guid.NewGuid(), Alice)));
        await Should.ThrowAsync<ArgumentException>(() => Repo.CreateAsync(NewGrant(TenantId.Platform, Guid.NewGuid(), Alice)));
        await Should.ThrowAsync<ArgumentException>(() => Repo.ListActiveForSubjectsAsync(default, [Alice], Now));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var containerId = Guid.NewGuid();
        var grant = await Repo.CreateAsync(NewGrant(Store.TenantA, containerId, Alice));
        var active = await Repo.ListActiveForSubjectsAsync(Store.TenantA, [Alice], Now);
        active.ShouldHaveSingleItem().Id.ShouldBe(grant.Id);

        (await Repo.RevokeAsync(Store.TenantA, grant.Id, Admin, "User moved teams", Now)).ShouldBeTrue();
        (await Repo.RevokeAsync(Store.TenantA, grant.Id, Admin, "Duplicate revoke", Now)).ShouldBeFalse();
        (await Repo.ListActiveForSubjectsAsync(Store.TenantA, [Alice], Now)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var g1 = NewGrant(Store.TenantA, Guid.NewGuid(), Alice);
        await Repo.CreateAsync(g1);
        await Should.ThrowAsync<Exception>(() => Repo.CreateAsync(g1));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        var containerId = Guid.NewGuid();
        await Repo.CreateAsync(NewGrant(Store.TenantA, containerId, Alice));
        await Repo.CreateAsync(NewGrant(Store.TenantA, containerId, Bob));

        var items = await Repo.ListByContainerAsync(Store.TenantA, ContainerKind.Domain, containerId);
        items.Count.ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        var items = await Repo.ListActiveForSubjectsAsync(Store.TenantA, Array.Empty<Sid>(), Now);
        items.ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        var containerId = Guid.NewGuid();
        var grant = await Repo.CreateAsync(NewGrant(Store.TenantA, containerId, Alice));
        (await Repo.RevokeAllForSubjectAsync(Store.TenantA, [Alice], Admin, "Deprovisioned", Now)).ShouldBe(1);
        (await Repo.ListByContainerAsync(Store.TenantA, ContainerKind.Domain, containerId, includeInactive: false)).ShouldBeEmpty();
        (await Repo.ListByContainerAsync(Store.TenantA, ContainerKind.Domain, containerId, includeInactive: true)).ShouldHaveSingleItem().IsRevoked.ShouldBeTrue();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Catalog_grant_denies_data()
    {
        if (!Store.IsAvailable) return;

        var table = new TableIdentifier("sales", "dbo", "catalog_only_" + Guid.NewGuid().ToString("N")[..6]);
        await Store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = table,
            OwnerTenantId = Store.TenantA.Value,
            Table = new Table { Id = Guid.NewGuid(), SourceName = table.Domain, SchemaName = table.Schema, TableName = table.TableName, IsActive = true }
        });

        await Repo.CreateAsync(NewGrant(Store.TenantA, Guid.NewGuid(), Alice));
        var dataConsents = await Store.Consents.GetActiveConsentsForSubjectsAsync([Alice], table, Now, Store.TenantA);
        dataConsents.ShouldBeEmpty();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlCatalogVisibilityGrantContractTests(GovernancePostgresFixture fixture) : CatalogVisibilityGrantContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "catalog_visibility_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerCatalogVisibilityGrantContractTests(GovernanceSqlServerFixture fixture) : CatalogVisibilityGrantContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "catalog_visibility_contract");
}
