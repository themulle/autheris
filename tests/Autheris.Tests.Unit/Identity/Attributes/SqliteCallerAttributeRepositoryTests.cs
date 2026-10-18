namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PLAN-GOV-129 WP-1: storage contract of the caller attribute registry (SQLite; PostgreSQL and SQL Server in the integration tests). BC-129-10: the repository guards the four-eyes steps itself.</summary>
public sealed class SqliteCallerAttributeRepositoryTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant_a");
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteGovernanceRepository _store = GovernanceStoreFactory.CreateSqlite(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=ca_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));

    private ICallerAttributeRepository Repo => _store;

    public void Dispose() => _store.Dispose();

    internal static CallerAttributeDefinition Definition(string name = "cost_center", string description = "v1", TenantId? tenant = null) => new(
        Guid.NewGuid(), tenant ?? Tenant, name, CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60,
        new CallerAttributeProviderBinding("claims", new Dictionary<string, string> { ["claim"] = "department" }), description, FilterApprovalStatus.PendingApproval);

    [Fact]
    public async Task NewDefinition_IsStoredPending_AndRoundTrips()
    {
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

    [Fact]
    public async Task Approve_BySameSubject_IsRefused_ByTheRepositoryItself()
    {
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);

        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "ALICE", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.SameSubject);
        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single().Definition.Status.ShouldBe(FilterApprovalStatus.PendingApproval);
    }

    [Fact]
    public async Task Approve_WithTheHashTheApproverSaw_Activates_AndAStaleHashIsRefused()
    {
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);

        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", "stale", Now)).ShouldBe(CallerAttributeChangeResult.HashMismatch);
        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.Applied);

        var record = (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single();
        record.Definition.Status.ShouldBe(FilterApprovalStatus.Active);
        record.ApprovedBy.ShouldBe("bob");
    }

    [Fact]
    public async Task Change_OfAnActiveDefinition_WaitsAsDraft_AndTheActiveVersionStaysLastKnownGood()
    {
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now);

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
    }

    [Fact]
    public async Task Deletion_NeedsASecondPerson_AndRejectClearsIt()
    {
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

    [Fact]
    public async Task Approve_WithNothingPending_AndForAnUnknownName_AreDistinguished()
    {
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        await Repo.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now);

        (await Repo.ApproveDefinitionAsync(Tenant, def.Name, "carol", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.NothingPending);
        (await Repo.ApproveDefinitionAsync(Tenant, "nope", "carol", "x", Now)).ShouldBe(CallerAttributeChangeResult.NotFound);
    }

    [Fact]
    public async Task Rejected_FirstVersion_IsNotActive()
    {
        var def = Definition();
        await Repo.SubmitDefinitionAsync(def, "alice", Now);
        await Repo.RejectDefinitionAsync(Tenant, def.Name, "bob", Now);

        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Single().Definition.Status.ShouldBe(FilterApprovalStatus.Rejected);
    }

    [Fact]
    public async Task Tenants_AreSeparate()
    {
        await Repo.SubmitDefinitionAsync(Definition(), "alice", Now);
        await Repo.SubmitDefinitionAsync(Definition(tenant: new TenantId("tenant_b")), "alice", Now);

        (await Repo.LoadCallerAttributeSnapshotAsync()).Attributes.Select(a => a.Definition.Tenant.Value).Order().ShouldBe(["tenant_a", "tenant_b"]);
    }

    [Fact]
    public async Task ServiceSet_FollowsTheSameFourEyesFlow_AndCanonicalisesTheApplicationId()
    {
        var app = "AAAAAAAA-0000-0000-0000-000000000001";
        await Repo.SubmitServiceSetAsync(Tenant, app, "cost_center", ["100", "200"], "alice", Now);
        var pending = (await Repo.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single();
        pending.Set.Status.ShouldBe(FilterApprovalStatus.PendingApproval);
        pending.Set.ApplicationId.ShouldBe(app.ToLowerInvariant());

        var hash = pending.Set.ComputeValuesHash();
        (await Repo.ApproveServiceSetAsync(Tenant, app.ToLowerInvariant(), "cost_center", "alice", hash, Now)).ShouldBe(CallerAttributeChangeResult.SameSubject);
        (await Repo.ApproveServiceSetAsync(Tenant, app, "cost_center", "bob", "stale", Now)).ShouldBe(CallerAttributeChangeResult.HashMismatch);
        (await Repo.ApproveServiceSetAsync(Tenant, app, "cost_center", "bob", hash, Now)).ShouldBe(CallerAttributeChangeResult.Applied);

        await Repo.SubmitServiceSetAsync(Tenant, app, "cost_center", ["300"], "carol", Now);
        var withDraft = (await Repo.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single();
        withDraft.Set.Values.ShouldBe(["100", "200"]);
        withDraft.DraftValues.ShouldBe(["300"]);
        await Repo.RejectServiceSetAsync(Tenant, app, "cost_center", "bob", Now);
        (await Repo.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single().DraftValues.ShouldBeNull();
    }
}
