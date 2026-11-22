using System;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Contract suite for <c>IAccessPolicySettingsRepository</c> across PostgreSQL and SQL Server engines.</summary>
public abstract class AccessPolicySettingsContract : GovernanceContractBase
{
    private static readonly Sid Alice = new("S-1-5-21-1001");
    private static readonly Sid Bob = new("S-1-5-21-1002");

    private IAccessPolicySettingsRepository Repo => Store.AccessPolicySettings;

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        await Repo.UpsertAsync(Store.TenantA, new TenantAccessPolicyUpdate(MaxSelfGrantDays: 30), expectedVersion: 0, Alice);

        var loadedA = await Repo.GetAsync(Store.TenantA);
        var loadedB = await Repo.GetAsync(Store.TenantB);

        loadedA.ShouldNotBeNull();
        loadedA.Settings.MaxSelfGrantDays.ShouldBe(30);
        loadedB.ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        await Repo.UpsertAsync(Store.TenantA, new TenantAccessPolicyUpdate(MaxSelfGrantDays: 45), expectedVersion: 0, Alice);
        await Repo.UpsertAsync(Store.TenantB, new TenantAccessPolicyUpdate(MaxSelfGrantDays: 90), expectedVersion: 0, Bob);

        var a = await Repo.GetAsync(Store.TenantA);
        var b = await Repo.GetAsync(Store.TenantB);

        a.ShouldNotBeNull();
        a.Settings.MaxSelfGrantDays.ShouldBe(45);
        b.ShouldNotBeNull();
        b.Settings.MaxSelfGrantDays.ShouldBe(90);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(() => Repo.GetAsync(default));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetAsync(TenantId.Platform));
        await Should.ThrowAsync<ArgumentException>(() => Repo.UpsertAsync(default, new TenantAccessPolicyUpdate(), 0, Alice));
        await Should.ThrowAsync<ArgumentException>(() => Repo.UpsertAsync(TenantId.Platform, new TenantAccessPolicyUpdate(), 0, Alice));
        await Should.ThrowAsync<ArgumentException>(() => Repo.DeleteIfVersionAsync(default, 1));
        await Should.ThrowAsync<ArgumentException>(() => Repo.DeleteIfVersionAsync(TenantId.Platform, 1));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var created = await Repo.UpsertAsync(
            Store.TenantA,
            new TenantAccessPolicyUpdate(MaxSelfGrantDays: 120, SelfGrantCeiling: ProtectionClass.Internal),
            expectedVersion: 0,
            Alice);
        created.Status.ShouldBe(AccessPolicyWriteStatus.Created);
        created.Stored!.Version.ShouldBe(1);

        var updated = await Repo.UpsertAsync(
            Store.TenantA,
            new TenantAccessPolicyUpdate(MaxSelfGrantDays: 60, SelfGrantCeiling: ProtectionClass.Internal),
            expectedVersion: 1,
            Bob);
        updated.Status.ShouldBe(AccessPolicyWriteStatus.Updated);
        updated.Stored!.Version.ShouldBe(2);

        var stale = await Repo.UpsertAsync(
            Store.TenantA,
            new TenantAccessPolicyUpdate(MaxSelfGrantDays: 10),
            expectedVersion: 1,
            Bob);
        stale.Status.ShouldBe(AccessPolicyWriteStatus.Conflict);

        (await Repo.DeleteIfVersionAsync(Store.TenantA, 1)).ShouldBeFalse();
        (await Repo.DeleteIfVersionAsync(Store.TenantA, 2)).ShouldBeTrue();
        (await Repo.GetAsync(Store.TenantA)).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var results = await Task.WhenAll(
            Repo.UpsertAsync(Store.TenantA, new TenantAccessPolicyUpdate(MaxSelfGrantDays: 30), 0, Alice),
            Repo.UpsertAsync(Store.TenantA, new TenantAccessPolicyUpdate(MaxSelfGrantDays: 60), 0, Bob));

        results.Count(r => r.Status == AccessPolicyWriteStatus.Created).ShouldBe(1);
        results.Count(r => r.Status == AccessPolicyWriteStatus.Conflict).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var current = await Repo.GetAsync(Store.TenantA);
        current.ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        await Repo.UpsertAsync(Store.TenantA, new TenantAccessPolicyUpdate(MaxPageSize: 250), 0, Alice);
        var loaded = await Repo.GetAsync(Store.TenantA);
        loaded!.Settings.MaxPageSize.ShouldBe(250);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        (await Repo.GetAsync(Store.TenantB)).ShouldBeNull();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlAccessPolicySettingsContractTests(GovernancePostgresFixture fixture) : AccessPolicySettingsContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "access_policy_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerAccessPolicySettingsContractTests(GovernanceSqlServerFixture fixture) : AccessPolicySettingsContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "access_policy_contract");
}
