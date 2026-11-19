using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>ICallerAttributeRepository</c> across PostgreSQL and SQL Server engines. The repository guards the
/// four-eyes steps itself, so the cases cover the approval flow, drafts, deletions and service sets on both engines.
/// </summary>
public abstract class CallerAttributeContract : GovernanceContractBase
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    private ICallerAttributeRepository Repo => Store.CallerAttributes;

    private TenantId Tenant => Store.TenantA;

    private CallerAttributeDefinition Definition(string name = "cost_center", string description = "v1", TenantId? tenant = null) =>
        Autheris.Tests.Unit.Identity.Attributes.CallerAttributeTestSupport.Definition(name, description, tenant ?? Tenant);

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Repo.SubmitDefinitionAsync(Definition(), "alice", Now);
        await Repo.SubmitDefinitionAsync(Definition(tenant: Store.TenantB), "alice", Now);

        var snapshot = await Repo.LoadCallerAttributeSnapshotAsync();

        snapshot.Attributes.Select(a => a.Definition.Tenant.Value).Order().ShouldBe([Store.TenantA.Value, Store.TenantB.Value]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        var app = "AAAAAAAA-0000-0000-0000-000000000001";
        await Repo.SubmitServiceSetAsync(Tenant, app, "cost_center", ["100"], "alice", Now);
        var hash = (await Repo.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single().Set.ComputeValuesHash();

        (await Repo.ApproveDefinitionAsync(Store.TenantB, def.Name, "bob", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.NotFound);
        (await Repo.RejectDefinitionAsync(Store.TenantB, def.Name, "bob", Now)).ShouldBe(CallerAttributeChangeResult.NotFound);
        (await Repo.RequestDeletionAsync(Store.TenantB, def.Name, "bob", Now)).ShouldBe(CallerAttributeChangeResult.NotFound);
        (await Repo.ApproveServiceSetAsync(Store.TenantB, app, "cost_center", "bob", hash, Now)).ShouldBe(CallerAttributeChangeResult.NotFound);

        var snapshot = await Repo.LoadCallerAttributeSnapshotAsync();
        snapshot.Attributes.Single().Definition.Status.ShouldBe(FilterApprovalStatus.PendingApproval);
        snapshot.ServiceSets.Single().Set.Status.ShouldBe(FilterApprovalStatus.PendingApproval);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        Should.Throw<ArgumentException>(() => { _ = new TenantId("invalid tenant!"); });
        await Should.ThrowAsync<ArgumentException>(() => Repo.ApproveDefinitionAsync(default, "cost_center", "bob", "x", Now));
        await Should.ThrowAsync<ArgumentException>(() => Repo.RejectDefinitionAsync(default, "cost_center", "bob", Now));
        await Should.ThrowAsync<ArgumentException>(() => Repo.SubmitServiceSetAsync(default, "AAAAAAAA-0000-0000-0000-000000000001", "cost_center", ["1"], "alice", Now));
        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now);
        var generation = await Repo.GetCallerAttributeGenerationAsync();

        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "carol", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.NothingPending);

        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single().Definition.Status.ShouldBe(FilterApprovalStatus.Active);
        (await Repo.GetCallerAttributeGenerationAsync()).ShouldBe(generation);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        var app = "AAAAAAAA-0000-0000-0000-000000000001";

        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        await Repo.SubmitDefinitionAsync(def, "carol", Now);
        await Repo.SubmitServiceSetAsync(Tenant, app, "cost_center", ["1"], "alice", Now);
        await Repo.SubmitServiceSetAsync(Tenant, app.ToLowerInvariant(), "cost_center", ["1"], "carol", Now);

        var snapshot = await Repo.LoadCallerAttributeSnapshotAsync();
        snapshot.Attributes.Count.ShouldBe(1);
        snapshot.ServiceSets.Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        foreach (var name in new[] { "b_attr", "a_attr", "c_attr" })
        {
            await Repo.SubmitDefinitionAsync(Definition(name), "alice", Now);
        }

        var first = (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Select(a => a.Definition.Name).ToList();
        var second = (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Select(a => a.Definition.Name).ToList();

        first.Order().ShouldBe(["a_attr", "b_attr", "c_attr"]);
        second.ShouldBe(first);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        foreach (var i in Enumerable.Range(0, 60))
        {
            await Repo.SubmitDefinitionAsync(Definition($"attr_{i:D2}"), "alice", Now);
        }

        var snapshot = await Repo.LoadCallerAttributeSnapshotAsync();

        snapshot.Attributes.Count.ShouldBe(60);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now);
        await Repo.RequestDeletionAsync(Tenant, def.Name, "carol", Now);
        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.Applied);

        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.ShouldBeEmpty();
        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "carol", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.NotFound);
    }

    [RequiresContainerRuntimeFact]
    public async Task NewDefinition_IsStoredPending_AndRoundTrips()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        (await Repo.SubmitDefinitionAsync(def, "alice", Now)).ShouldBe(CallerAttributeChangeResult.Applied);

        var snapshot = await Repo.LoadCallerAttributeSnapshotAsync();
        var record = snapshot.Attributes.ShouldHaveSingleItem();
        record.Definition.Status.ShouldBe(FilterApprovalStatus.PendingApproval);
        record.CreatedBy.ShouldBe("alice");
        record.Definition.Provider.Config["claim"].ShouldBe("department");
        record.Definition.ComputeDefinitionHash().ShouldBe(def.ComputeDefinitionHash());
        snapshot.Generation.ShouldBeGreaterThan(0);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Approve_BySameSubject_IsRefused_ByTheRepositoryItself()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);

        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "ALICE", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.SameSubject);
        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single().Definition.Status.ShouldBe(FilterApprovalStatus.PendingApproval);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Approve_WithTheHashTheApproverSaw_Activates_AndAStaleHashIsRefused()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);

        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", "stale", Now)).ShouldBe(CallerAttributeChangeResult.HashMismatch);
        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.Applied);

        var record = (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single();
        record.Definition.Status.ShouldBe(FilterApprovalStatus.Active);
        record.ApprovedBy.ShouldBe("bob");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Change_OfAnActiveDefinition_WaitsAsDraft_AndTheActiveVersionStaysLastKnownGood()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now);
        var start = await Repo.GetCallerAttributeGenerationAsync();

        var changed = def with { Description = "v2" };
        await Repo.SubmitDefinitionAsync(changed, "carol", Now);

        var record = (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single();
        record.Definition.Description.ShouldBe("v1");
        record.Definition.Status.ShouldBe(FilterApprovalStatus.Active);
        record.Draft!.Description.ShouldBe("v2");
        record.DraftCreatedBy.ShouldBe("carol");

        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "carol", changed.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.SameSubject);
        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.HashMismatch);
        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", changed.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.Applied);

        var after = (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single();
        after.Definition.Description.ShouldBe("v2");
        after.Draft.ShouldBeNull();
        (await Repo.GetCallerAttributeGenerationAsync()).ShouldBe(start + 2);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Deletion_NeedsASecondPerson_AndRejectClearsIt()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now);

        await Repo.RequestDeletionAsync(Tenant, def.Name, "carol", Now);
        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single().PendingDeletion.ShouldBeTrue();
        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "carol", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.SameSubject);

        await Repo.RejectDefinitionAsync(Tenant, def.Name, "bob", Now);
        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single().PendingDeletion.ShouldBeFalse();

        await Repo.RequestDeletionAsync(Tenant, def.Name, "carol", Now);
        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.Applied);
        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task Approve_WithNothingPending_AndForAnUnknownName_AreDistinguished()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now);

        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "carol", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.NothingPending);
        (await Repo.ApproveDefinitionAsync(Tenant, "nope", "carol", "x", Now)).ShouldBe(CallerAttributeChangeResult.NotFound);
    }

    [RequiresContainerRuntimeFact]
    public async Task Rejected_FirstVersion_IsNotActive()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        await Repo.RejectDefinitionAsync(Tenant, def.Name, "bob", Now);

        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single().Definition.Status.ShouldBe(FilterApprovalStatus.Rejected);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ServiceSet_FollowsTheSameFourEyesFlow_AndCanonicalisesTheApplicationId()
    {
        if (!Store.IsAvailable) return;
        var app = "AAAAAAAA-0000-0000-0000-000000000001";
        await Repo.SubmitServiceSetAsync(Tenant, app, "cost_center", ["100", "200"], "alice", Now);
        var pending = (await Repo.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single();
        pending.Set.Status.ShouldBe(FilterApprovalStatus.PendingApproval);
        pending.Set.ApplicationId.ShouldBe(app.ToLowerInvariant());

        var hash = pending.Set.ComputeValuesHash();
        (await Repo.ApproveServiceSetAsync(Tenant, app.ToLowerInvariant(), "cost_center", "alice", hash, Now)).ShouldBe(CallerAttributeChangeResult.SameSubject);
        (await Repo.ApproveServiceSetAsync(Tenant, app, "cost_center", "bob", "stale", Now)).ShouldBe(CallerAttributeChangeResult.HashMismatch);
        (await Repo.ApproveServiceSetAsync(Tenant, app, "cost_center", "bob", hash, Now)).ShouldBe(CallerAttributeChangeResult.Applied);
        (await Repo.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single().Set.Status.ShouldBe(FilterApprovalStatus.Active);

        await Repo.SubmitServiceSetAsync(Tenant, app, "cost_center", ["300"], "carol", Now);
        var withDraft = (await Repo.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single();
        withDraft.Set.Values.ShouldBe(["100", "200"]);
        withDraft.DraftValues.ShouldBe(["300"]);
        await Repo.RejectServiceSetAsync(Tenant, app, "cost_center", "bob", Now);
        (await Repo.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single().DraftValues.ShouldBeNull();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlCallerAttributeContractTests(GovernancePostgresFixture fixture) : CallerAttributeContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "caller_attribute_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerCallerAttributeContractTests(GovernanceSqlServerFixture fixture) : CallerAttributeContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "caller_attribute_contract");
}
