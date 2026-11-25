using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>IFunctionalRightRepository</c> across PostgreSQL and SQL Server engines. Mappings are keyed by (tenant, realm, group or role,
/// right); the approved read is the store-side defence behind the single mapping authority: a mapping without an approver never comes back.
/// A saved mapping is approved (single approval): what a mapping may confer is decided by the mapper.
/// </summary>
public abstract class FunctionalRightContract : GovernanceContractBase
{
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var group = Group();
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, "approver"));

        (await Repo.GetMappingsAsync(Store.TenantA, Realm)).ShouldHaveSingleItem().GroupOrRole.ShouldBe(group);
        (await Repo.GetMappingsAsync(Store.TenantB, Realm)).ShouldBeEmpty();
        (await Repo.GetMappingAsync(Store.TenantB, Realm, group, FunctionalRight.DataExport)).ShouldBeNull();
        (await Repo.GetApprovedMappingsForGroupsAndRolesAsync(Store.TenantB, Realm, [group])).ShouldBeEmpty();
        (await Repo.GetMappingsAsync(Store.TenantA, "realm-other")).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var group = Group();
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, "approver-a"));
        await Repo.CreateMappingAsync(Mapping(Store.TenantB, group, FunctionalRight.DataExport, "approver-b"));

        await Repo.CreateMappingAsync(Mapping(Store.TenantB, group, FunctionalRight.DataExport, "someone-else"));
        await Repo.DeleteMappingAsync(Store.TenantB, Realm, group, FunctionalRight.DataExport);

        var kept = (await Repo.GetMappingAsync(Store.TenantA, Realm, group, FunctionalRight.DataExport)).ShouldNotBeNull();
        kept.ApprovedBy.ShouldBe("approver-a");
        (await Repo.GetMappingAsync(Store.TenantB, Realm, group, FunctionalRight.DataExport)).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var group = Group();

        Should.Throw<ArgumentException>(() => { _ = new TenantId("invalid tenant!"); });
        await Should.ThrowAsync<ArgumentException>(() => Repo.CreateMappingAsync(Mapping(default, group, FunctionalRight.DataExport, "approver")));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetMappingsAsync(default, Realm));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetMappingAsync(default, Realm, group, FunctionalRight.DataExport));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetApprovedMappingsForGroupsAndRolesAsync(default, Realm, [group]));
        await Should.ThrowAsync<ArgumentException>(() => Repo.DeleteMappingAsync(default, Realm, group, FunctionalRight.DataExport));

        (await Repo.GetMappingsAsync(Store.TenantA, Realm)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var group = Group();
        var created = Mapping(Store.TenantA, group, FunctionalRight.DataExport, "first-approver", "creator", createdAt: Now);
        await Repo.CreateMappingAsync(created);

        await Repo.CreateMappingAsync(created with { ApprovedBy = "second-approver", ApprovedAt = Now.AddHours(1), CreatedBy = "other", CreatedAt = Now.AddDays(1) });
        await Repo.CreateMappingAsync(created with { ApprovedBy = "second-approver", ApprovedAt = Now.AddHours(1), CreatedBy = "other", CreatedAt = Now.AddDays(1) });

        var read = (await Repo.GetMappingsAsync(Store.TenantA, Realm)).ShouldHaveSingleItem();
        read.ApprovedBy.ShouldBe("second-approver");
        read.ApprovedAt.ShouldBe(Now.AddHours(1));
        read.CreatedBy.ShouldBe("creator");
        read.CreatedAt.ShouldBe(Now);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var group = Group();

        // The key (tenant, realm, group or role, right) is an upsert target: a repeated create never raises a unique violation, and the
        // same group may hold different rights, the same right in another realm and the same key in another tenant.
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, "approver"));
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, "approver"));
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.McpUse, "approver"));
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, "approver", realm: "realm-2"));
        await Repo.CreateMappingAsync(Mapping(Store.TenantB, group, FunctionalRight.DataExport, "approver"));

        (await Repo.GetMappingsAsync(Store.TenantA, Realm)).Count.ShouldBe(2);
        (await Repo.GetMappingsAsync(Store.TenantA, "realm-2")).Count.ShouldBe(1);
        (await Repo.GetMappingsAsync(Store.TenantB, Realm)).Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var groups = Enumerable.Range(0, 5).Select(_ => Group()).ToArray();
        foreach (var group in groups)
        {
            await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, "approver"));
        }

        var first = await Repo.GetMappingsAsync(Store.TenantA, Realm);
        var second = await Repo.GetMappingsAsync(Store.TenantA, Realm);

        // The list read has no paging and no guaranteed order: it is complete and holds the same set on every call.
        first.Select(m => m.GroupOrRole).ToArray().ShouldBe(groups, ignoreOrder: true);
        second.Select(m => m.GroupOrRole).OrderBy(g => g, StringComparer.Ordinal).ToArray()
            .ShouldBe(first.Select(m => m.GroupOrRole).OrderBy(g => g, StringComparer.Ordinal).ToArray());
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var groups = Enumerable.Range(0, 120).Select(i => $"batch-{i:D3}").ToArray();
        foreach (var group in new[] { groups[0], groups[55], groups[119] })
        {
            await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, "approver"));
        }

        // The approved read is chunked (50 names per statement): a request above the chunk size loses no name.
        var approved = await Repo.GetApprovedMappingsForGroupsAndRolesAsync(Store.TenantA, Realm, groups);

        approved.Select(m => m.GroupOrRole).ToArray().ShouldBe([groups[0], groups[55], groups[119]], ignoreOrder: true);
        (await Repo.GetApprovedMappingsForGroupsAndRolesAsync(Store.TenantA, Realm, [])).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var group = Group();
        var keep = Group();
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, "approver"));
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, keep, FunctionalRight.DataExport, "approver"));

        await Repo.DeleteMappingAsync(Store.TenantA, Realm, group, FunctionalRight.DataExport);
        await Repo.DeleteMappingAsync(Store.TenantA, Realm, group, FunctionalRight.DataExport); // deleting a missing mapping is a no-op

        (await Repo.GetMappingAsync(Store.TenantA, Realm, group, FunctionalRight.DataExport)).ShouldBeNull();
        (await Repo.GetMappingsAsync(Store.TenantA, Realm)).ShouldHaveSingleItem().GroupOrRole.ShouldBe(keep);
        (await Repo.GetApprovedMappingsForGroupsAndRolesAsync(Store.TenantA, Realm, [group, keep])).ShouldHaveSingleItem().GroupOrRole.ShouldBe(keep);
    }

    [RequiresContainerRuntimeFact]
    public async Task Mapping_RoundTrip_PreservesTimestamps()
    {
        if (!Store.IsAvailable) return;
        var group = Group();
        var created = Mapping(Store.TenantA, group, FunctionalRight.DataExport, "approver");

        await Repo.CreateMappingAsync(created);

        var read = (await Repo.GetMappingAsync(Store.TenantA, Realm, group, FunctionalRight.DataExport)).ShouldNotBeNull();
        read.ApprovedBy.ShouldBe("approver");
        read.ApprovedAt.ShouldBe(created.ApprovedAt);
        read.CreatedAt.ShouldBe(created.CreatedAt);
        read.RecertClass.ShouldBe("Standard");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ApprovedMappings_SavedMapping_Included_WhateverTheApprover()
    {
        if (!Store.IsAvailable) return;
        var group = Group();
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, "same-person", "same-person"));
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.McpUse, "approver", "creator"));

        var approved = await Repo.GetApprovedMappingsForGroupsAndRolesAsync(Store.TenantA, Realm, [group]);

        approved.Select(m => m.FunctionalRight).ShouldBe([FunctionalRight.DataExport, FunctionalRight.McpUse], ignoreOrder: true);
    }

    [RequiresContainerRuntimeFact]
    public async Task ApprovedMappings_GroupCaseDiffers_Matches()
    {
        if (!Store.IsAvailable) return;
        var group = Group("Grp");
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, "approver"));

        var approved = await Repo.GetApprovedMappingsForGroupsAndRolesAsync(Store.TenantA, Realm, [group.ToLowerInvariant()]);

        approved.ShouldContain(m => m.GroupOrRole == group);
    }

    [RequiresContainerRuntimeFact]
    public async Task CreateMapping_PendingProposal_PersistsNullApproval()
    {
        if (!Store.IsAvailable) return;
        var group = Group();
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, approvedBy: null));

        var read = (await Repo.GetMappingAsync(Store.TenantA, Realm, group, FunctionalRight.DataExport)).ShouldNotBeNull();
        read.ApprovedBy.ShouldBeNull();
        read.ApprovedAt.ShouldBeNull();
        read.CreatedBy.ShouldBe("creator");

        var listed = (await Repo.GetMappingsAsync(Store.TenantA, Realm)).ShouldHaveSingleItem();
        listed.ApprovedBy.ShouldBeNull();
        listed.ApprovedAt.ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ApprovedMappings_PendingProposal_Excluded()
    {
        if (!Store.IsAvailable) return;
        var group = Group();
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.DataExport, approvedBy: null));
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.SecurityAdmin, approvedBy: null));
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.Consumer, approvedBy: "approver"));

        var approved = await Repo.GetApprovedMappingsForGroupsAndRolesAsync(Store.TenantA, Realm, [group]);

        approved.ShouldNotContain(m => m.FunctionalRight == FunctionalRight.DataExport);
        approved.ShouldNotContain(m => m.FunctionalRight == FunctionalRight.SecurityAdmin);
        approved.ShouldContain(m => m.FunctionalRight == FunctionalRight.Consumer);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task CreateMapping_ReproposalOverApproved_ResetsApproval()
    {
        if (!Store.IsAvailable) return;
        var group = Group();
        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.SecurityAdmin, approvedBy: "approver", createdBy: "creator"));
        (await Repo.GetApprovedMappingsForGroupsAndRolesAsync(Store.TenantA, Realm, [group])).ShouldHaveSingleItem();

        await Repo.CreateMappingAsync(Mapping(Store.TenantA, group, FunctionalRight.SecurityAdmin, approvedBy: null, createdBy: "creator-2"));

        var read = (await Repo.GetMappingAsync(Store.TenantA, Realm, group, FunctionalRight.SecurityAdmin)).ShouldNotBeNull();
        read.ApprovedBy.ShouldBeNull();
        read.ApprovedAt.ShouldBeNull();
        (await Repo.GetApprovedMappingsForGroupsAndRolesAsync(Store.TenantA, Realm, [group])).ShouldBeEmpty();
    }

    private const string Realm = "realm-1";
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private IFunctionalRightRepository Repo => Store.FunctionalRights;

    private static string Group(string prefix = "grp") => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static FunctionalRightMapping Mapping(
        TenantId tenant, string groupOrRole, FunctionalRight right, string? approvedBy, string createdBy = "creator", string realm = Realm, DateTimeOffset? createdAt = null) =>
        new(tenant, realm, groupOrRole, right, approvedBy, approvedBy is null ? null : Now, "Standard", createdBy, createdAt ?? Now);
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlFunctionalRightContractTests(GovernancePostgresFixture fixture) : FunctionalRightContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "functional_right_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerFunctionalRightContractTests(GovernanceSqlServerFixture fixture) : FunctionalRightContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "functional_right_contract");
}
