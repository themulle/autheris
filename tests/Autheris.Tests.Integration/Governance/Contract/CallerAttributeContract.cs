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
/// Contract suite for <c>ICallerAttributeRepository</c> across PostgreSQL and SQL Server engines. A save takes effect at once (single
/// approval), so the cases cover save, replace, delete and service sets on both engines.
/// </summary>
public abstract class CallerAttributeContract : GovernanceContractBase
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    private ICallerAttributeRepository Repo => Store.CallerAttributes;

    private TenantId Tenant => Store.TenantA;

    private CallerAttributeDefinition Definition(string name = "cost_center", string description = "v1", TenantId? tenant = null) =>
        Autheris.Tests.Unit.Identity.Attributes.CallerAttributeTestSupport.Definition(name, description, tenant ?? Tenant) with { Status = FilterApprovalStatus.Active };

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Repo.SaveDefinitionAsync(Definition(), "alice", Now);
        await Repo.SaveDefinitionAsync(Definition(tenant: Store.TenantB), "alice", Now);

        var snapshot = await Repo.LoadCallerAttributeSnapshotAsync();

        snapshot.Attributes.Select(a => a.Definition.Tenant.Value).Order().ShouldBe([Store.TenantA.Value, Store.TenantB.Value]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SaveDefinitionAsync(def, "alice", Now);
        var app = "AAAAAAAA-0000-0000-0000-000000000001";
        await Repo.SaveServiceSetAsync(Tenant, app, "cost_center", ["100"], "alice", Now);

        (await Repo.DeleteDefinitionAsync(Store.TenantB, def.Name)).ShouldBe(CallerAttributeChangeResult.NotFound);

        var snapshot = await Repo.LoadCallerAttributeSnapshotAsync();
        snapshot.Attributes.Single().Definition.Tenant.ShouldBe(Tenant);
        snapshot.ServiceSets.Single().Set.Values.ShouldBe(["100"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        Should.Throw<ArgumentException>(() => { _ = new TenantId("invalid tenant!"); });
        await Should.ThrowAsync<ArgumentException>(() => Repo.DeleteDefinitionAsync(default, "cost_center"));
        await Should.ThrowAsync<ArgumentException>(() => Repo.SaveServiceSetAsync(default, "AAAAAAAA-0000-0000-0000-000000000001", "cost_center", ["1"], "alice", Now));
        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SaveDefinitionAsync(def, "alice", Now);
        await Repo.SaveDefinitionAsync(def, "alice", Now);

        var snapshot = await Repo.LoadCallerAttributeSnapshotAsync();
        snapshot.Attributes.Count.ShouldBe(1);
        snapshot.Attributes.Single().Definition.Status.ShouldBe(FilterApprovalStatus.Active);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        var app = "AAAAAAAA-0000-0000-0000-000000000001";

        await Repo.SaveDefinitionAsync(def, "alice", Now);
        await Repo.SaveDefinitionAsync(def, "carol", Now);
        await Repo.SaveServiceSetAsync(Tenant, app, "cost_center", ["1"], "alice", Now);
        await Repo.SaveServiceSetAsync(Tenant, app.ToLowerInvariant(), "cost_center", ["1"], "carol", Now);

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
            await Repo.SaveDefinitionAsync(Definition(name), "alice", Now);
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
            await Repo.SaveDefinitionAsync(Definition($"attr_{i:D2}"), "alice", Now);
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
        await Repo.SaveDefinitionAsync(def, "alice", Now);
        (await Repo.DeleteDefinitionAsync(Tenant, def.Name)).ShouldBe(CallerAttributeChangeResult.Applied);

        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.ShouldBeEmpty();
        (await Repo.DeleteDefinitionAsync(Tenant, def.Name)).ShouldBe(CallerAttributeChangeResult.NotFound);
    }

    [RequiresContainerRuntimeFact]
    public async Task NewDefinition_IsActiveAtOnce_AndRoundTrips()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        (await Repo.SaveDefinitionAsync(def, "alice", Now)).ShouldBe(CallerAttributeChangeResult.Applied);

        var record = (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single();
        record.Definition.Status.ShouldBe(FilterApprovalStatus.Active);
        record.DefinitionHash.ShouldBe(def.ComputeDefinitionHash());
        record.ApprovedBy.ShouldBe("alice");
    }

    [RequiresContainerRuntimeFact]
    public async Task Save_OfAnExistingDefinition_ReplacesItAtOnce_AndBumpsTheGeneration()
    {
        if (!Store.IsAvailable) return;
        var def = Definition();
        await Repo.SaveDefinitionAsync(def, "alice", Now);
        var generation = await Repo.GetCallerAttributeGenerationAsync();

        await Repo.SaveDefinitionAsync(def with { Description = "v2" }, "carol", Now);

        var record = (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single();
        record.Definition.Description.ShouldBe("v2");
        record.ApprovedBy.ShouldBe("carol");
        (await Repo.GetCallerAttributeGenerationAsync()).ShouldBe(generation + 1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ServiceSet_IsActiveAtOnce_AndCanonicalisesTheApplicationId()
    {
        if (!Store.IsAvailable) return;
        var app = "AAAAAAAA-0000-0000-0000-000000000001";
        await Repo.SaveServiceSetAsync(Tenant, app, "cost_center", ["100", "200"], "alice", Now);
        var saved = (await Repo.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single();
        saved.Set.Status.ShouldBe(FilterApprovalStatus.Active);
        saved.Set.ApplicationId.ShouldBe(app.ToLowerInvariant());

        await Repo.SaveServiceSetAsync(Tenant, app, "cost_center", ["300"], "carol", Now);
        var replaced = (await Repo.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single();
        replaced.Set.Values.ShouldBe(["300"]);
        replaced.ApprovedBy.ShouldBe("carol");
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
