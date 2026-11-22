using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Contract suite for <c>IGovernanceDomainRepository</c> and <c>IGovernanceEpochStore</c> across PostgreSQL and SQL Server engines.</summary>
public abstract class GovernanceDomainContract : GovernanceContractBase
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly Sid Alice = new("S-1-5-21-1001");
    private static readonly Sid Bob = new("S-1-5-21-1002");

    private IGovernanceDomainRepository Repo => Store.GovernanceDomains;
    private IGovernanceEpochStore EpochStore => Store.Services.GetRequiredService<IGovernanceEpochStore>();

    private static GovernanceDomain NewDomain(
        TenantId tenant,
        string name,
        ProtectionClass? defaultClass = ProtectionClass.Confidential,
        CatalogVisibilityMode visibility = CatalogVisibilityMode.OnRequest,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), tenant, name, "desc", "owner@example.com", Alice, defaultClass, visibility, IsSystemUnassigned: false, Version: 0, T0, T0);

    private async Task<GovernanceDomain> CreateDomainAsync(
        TenantId tenant,
        string name,
        ProtectionClass? defaultClass = ProtectionClass.Confidential,
        CatalogVisibilityMode visibility = CatalogVisibilityMode.OnRequest)
    {
        var result = await Repo.UpsertAsync(NewDomain(tenant, name, defaultClass, visibility), expectedVersion: 0, Alice);
        result.Status.ShouldBe(GovernanceDomainWriteStatus.Created);
        return result.Domain!;
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var aDomain = await CreateDomainAsync(Store.TenantA, "finance-a");
        var bDomain = await CreateDomainAsync(Store.TenantB, "finance-b");
        var tableId = Guid.NewGuid();
        await Repo.AssignTablesAsync(Store.TenantA, aDomain.Id, [tableId], Alice);
        await Repo.SetProtectionAsync(Store.TenantA, tableId, "", new ObjectProtection(ProtectionClass.Secret, "PII_SSN"), Alice);

        (await Repo.GetAsync(Store.TenantA, aDomain.Id)).ShouldNotBeNull();
        (await Repo.GetAsync(Store.TenantA, bDomain.Id)).ShouldBeNull();
        (await Repo.GetAsync(Store.TenantB, aDomain.Id)).ShouldBeNull();
        (await Repo.ListAsync(Store.TenantA)).Select(d => d.Name).ShouldBe(["finance-a"]);
        (await Repo.ListAsync(Store.TenantB)).Select(d => d.Name).ShouldBe(["finance-b"]);

        var (tableProtB, colProtB) = await Repo.GetProtectionAsync(Store.TenantB, tableId);
        tableProtB.ShouldBeNull();
        colProtB.ShouldBeEmpty();
        (await Repo.GetContainerForTableAsync(Store.TenantB, tableId)).ShouldBe(ContainerDefaults.Unassigned);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var aDomain = await CreateDomainAsync(Store.TenantA, "shared-name");
        var tableId = Guid.NewGuid();
        await Repo.AssignTablesAsync(Store.TenantA, aDomain.Id, [tableId], Alice);

        (await Repo.UpsertAsync(aDomain with { Tenant = Store.TenantB, Description = "hijacked" }, expectedVersion: 1, Bob))
            .Status.ShouldBe(GovernanceDomainWriteStatus.NotFound);
        (await Repo.DeleteAsync(Store.TenantB, aDomain.Id, expectedVersion: 1)).ShouldBeFalse();
        await Should.ThrowAsync<KeyNotFoundException>(() => Repo.AssignTablesAsync(Store.TenantB, aDomain.Id, [tableId], Bob));

        var stillA = await Repo.GetAsync(Store.TenantA, aDomain.Id);
        stillA.ShouldNotBeNull();
        stillA.Description.ShouldBe("desc");
        stillA.Version.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        Should.Throw<ArgumentException>(() => { _ = new TenantId("invalid tenant!"); });

        await Should.ThrowAsync<ArgumentException>(() => Repo.ListAsync(default));
        await Should.ThrowAsync<ArgumentException>(() => Repo.ListAsync(TenantId.Platform));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetAsync(default, Guid.NewGuid()));
        await Should.ThrowAsync<ArgumentException>(() => Repo.UpsertAsync(NewDomain(default, "x"), 0, Alice));
        await Should.ThrowAsync<ArgumentException>(() => Repo.EnsureUnassignedAsync(default));
        await Should.ThrowAsync<ArgumentException>(() => Repo.AssignTablesAsync(default, Guid.NewGuid(), [Guid.NewGuid()], Alice));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetProtectionAsync(default, Guid.NewGuid()));
        await Should.ThrowAsync<ArgumentException>(() => Repo.SetProtectionAsync(default, Guid.NewGuid(), "", new ObjectProtection(ProtectionClass.Confidential), Alice));
        await Should.ThrowAsync<ArgumentException>(() => Repo.SetTableProtectionAsync(default, Guid.NewGuid(), new ObjectProtection(ProtectionClass.Confidential), null, Alice));
        await Should.ThrowAsync<ArgumentException>(() => Repo.DeleteAsync(default, Guid.NewGuid(), 1));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetContainerForTableAsync(default, Guid.NewGuid()));
        await Should.ThrowAsync<ArgumentException>(() => EpochStore.GetEpochAsync(default));
        await Should.ThrowAsync<ArgumentException>(() => EpochStore.BumpEpochAsync(default));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var u1 = await Repo.EnsureUnassignedAsync(Store.TenantA);
        var u2 = await Repo.EnsureUnassignedAsync(Store.TenantA);
        u1.Id.ShouldBe(Guid.Empty);
        u2.Id.ShouldBe(Guid.Empty);
        u2.Version.ShouldBe(1);

        var domain = await CreateDomainAsync(Store.TenantA, "hr");
        var tableId = Guid.NewGuid();
        await Repo.AssignTablesAsync(Store.TenantA, domain.Id, [tableId, tableId], Alice);
        await Repo.AssignTablesAsync(Store.TenantA, domain.Id, [tableId], Bob);
        (await Repo.GetContainerForTableAsync(Store.TenantA, tableId)).Container.Id.ShouldBe(domain.Id);

        var prot = new ObjectProtection(ProtectionClass.Confidential, "PII_HR");
        await Repo.SetProtectionAsync(Store.TenantA, tableId, "", prot, Alice);
        await Repo.SetProtectionAsync(Store.TenantA, tableId, "", prot, Bob);
        var (loadedTable, _) = await Repo.GetProtectionAsync(Store.TenantA, tableId);
        loadedTable.ShouldBe(prot);

        var updated = await Repo.UpsertAsync(domain with { Description = "v2" }, expectedVersion: 1, Alice);
        updated.Status.ShouldBe(GovernanceDomainWriteStatus.Updated);
        updated.Domain!.Version.ShouldBe(2);
        (await Repo.UpsertAsync(domain with { Description = "stale" }, expectedVersion: 1, Alice))
            .Status.ShouldBe(GovernanceDomainWriteStatus.Conflict);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var calls = Enumerable.Range(0, 4).Select(_ =>
            Repo.UpsertAsync(NewDomain(Store.TenantA, "race-domain"), expectedVersion: 0, Alice));
        var results = await Task.WhenAll(calls);
        results.Count(r => r.Status == GovernanceDomainWriteStatus.Created).ShouldBe(1);
        results.Count(r => r.Status == GovernanceDomainWriteStatus.Conflict).ShouldBe(3);

        var second = await CreateDomainAsync(Store.TenantA, "other-domain");
        (await Repo.UpsertAsync(second with { Name = "race-domain" }, expectedVersion: 1, Alice))
            .Status.ShouldBe(GovernanceDomainWriteStatus.Conflict);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        foreach (var name in new[] { "charlie", "alpha", "bravo" })
        {
            await CreateDomainAsync(Store.TenantA, name);
        }

        var page1 = await Repo.ListAsync(Store.TenantA, limit: 2, offset: 0);
        var page2 = await Repo.ListAsync(Store.TenantA, limit: 2, offset: 2);
        page1.Select(d => d.Name).ShouldBe(["alpha", "bravo"]);
        page2.Select(d => d.Name).ShouldBe(["charlie"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        await CreateDomainAsync(Store.TenantA, "only-domain");
        (await Repo.ListAsync(Store.TenantA, limit: int.MaxValue, offset: 0)).Count.ShouldBe(1);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Repo.ListAsync(Store.TenantA, limit: 0, offset: 0));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Repo.ListAsync(Store.TenantA, limit: -1, offset: 0));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Repo.ListAsync(Store.TenantA, limit: 10, offset: -1));

        var tooManyTables = Enumerable.Range(0, IGovernanceDomainRepository.MaxBatchAssignCount + 1).Select(_ => Guid.NewGuid()).ToArray();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Repo.AssignTablesAsync(Store.TenantA, Guid.Empty, tooManyTables, Alice));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var domain = await CreateDomainAsync(Store.TenantA, "temp-domain");
        var tableId = Guid.NewGuid();
        await Repo.AssignTablesAsync(Store.TenantA, domain.Id, [tableId], Alice);

        (await Repo.DeleteAsync(Store.TenantA, domain.Id, expectedVersion: 99)).ShouldBeFalse();
        (await Repo.DeleteAsync(Store.TenantA, domain.Id, expectedVersion: 1)).ShouldBeTrue();

        (await Repo.GetAsync(Store.TenantA, domain.Id)).ShouldBeNull();
        (await Repo.ListAsync(Store.TenantA)).ShouldBeEmpty();
        (await Repo.GetContainerForTableAsync(Store.TenantA, tableId)).ShouldBe(ContainerDefaults.Unassigned);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Protection_overrides_roundtrip_and_reject_public_mask_category()
    {
        if (!Store.IsAvailable) return;
        var tableId = Guid.NewGuid();
        await Repo.SetProtectionAsync(Store.TenantA, tableId, "", new ObjectProtection(ProtectionClass.Internal, "PII_GENERAL"), Alice);
        await Repo.SetProtectionAsync(Store.TenantA, tableId, "ssn", new ObjectProtection(ProtectionClass.Secret, "PII_SSN"), Alice);
        await Repo.SetProtectionAsync(Store.TenantA, tableId, "email", new ObjectProtection(ProtectionClass.Internal), Alice);

        await Should.ThrowAsync<ArgumentException>(() =>
            Repo.SetProtectionAsync(Store.TenantA, tableId, "bad", new ObjectProtection(ProtectionClass.Confidential, "PUBLIC"), Alice));

        await Repo.SetTableProtectionAsync(Store.TenantA, tableId, new ObjectProtection(ProtectionClass.Confidential, "PII_GENERAL"), ["ssn", "email"], Bob);

        var (tbl, cols) = await Repo.GetProtectionAsync(Store.TenantA, tableId);
        tbl.ShouldBe(new ObjectProtection(ProtectionClass.Confidential, "PII_GENERAL"));
        cols["ssn"].ShouldBe(new ObjectProtection(null, "PII_SSN"));
        cols.ContainsKey("email").ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Missing_assignment_falls_back_to_unassigned_and_system_domain_is_protected()
    {
        if (!Store.IsAvailable) return;
        var unassignedTable = Guid.NewGuid();

        var withoutRow = await Repo.GetContainerForTableAsync(Store.TenantA, unassignedTable);
        withoutRow.ShouldBe(ContainerDefaults.Unassigned);
        withoutRow.IsValid.ShouldBeFalse();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Repo.EnsureUnassignedAsync(Store.TenantA)));
        var withRow = await Repo.GetContainerForTableAsync(Store.TenantA, unassignedTable);
        withRow.IsValid.ShouldBeTrue();
        withRow.Container.ShouldBe(new ContainerRef(ContainerKind.Domain, Guid.Empty));
        withRow.DefaultClass.ShouldBe(ProtectionClass.Confidential);
        withRow.Visibility.ShouldBe(CatalogVisibilityMode.OnRequest);

        var unassigned = await Repo.GetAsync(Store.TenantA, Guid.Empty);
        unassigned.ShouldNotBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => Repo.UpsertAsync(unassigned with { OwnerGroupSid = Alice }, 1, Alice));
        await Should.ThrowAsync<InvalidOperationException>(() => Repo.DeleteAsync(Store.TenantA, Guid.Empty, 1));
        await Should.ThrowAsync<ArgumentException>(() => Repo.UpsertAsync(NewDomain(Store.TenantA, "bad-contact") with { Contact = "javascript:alert(1)" }, 0, Alice));

        var epochAfterEnsure = await EpochStore.GetEpochAsync(Store.TenantA);
        epochAfterEnsure.ShouldBeGreaterThan(0);
        var bumpedEpoch = await EpochStore.BumpEpochAsync(Store.TenantA);
        bumpedEpoch.ShouldBeGreaterThan(epochAfterEnsure);
        (await EpochStore.GetEpochAsync(Store.TenantA)).ShouldBe(bumpedEpoch);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlGovernanceDomainContractTests(GovernancePostgresFixture fixture) : GovernanceDomainContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "governance_domain_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerGovernanceDomainContractTests(GovernanceSqlServerFixture fixture) : GovernanceDomainContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "governance_domain_contract");
}
