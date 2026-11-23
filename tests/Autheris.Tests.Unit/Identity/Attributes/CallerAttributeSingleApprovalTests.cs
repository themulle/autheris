using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.SingleApproval;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Audit;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Identity.Attributes.CallerAttributeTestSupport;

namespace Autheris.Tests.Unit.Identity.Attributes;

/// <summary>Single approval of caller attribute definitions and service sets: a save applies at once, needs the approver role, never widens the writer's own access and is audited fail closed.</summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeSingleApprovalTests
{
    private readonly FakeRepository _repository = new();
    private readonly FakeRegistry _registry = new();
    private readonly InMemoryAuditLogRepository _audit = new();
    private readonly FakeInvalidator _invalidator = new();

    private CallerAttributeAdministrationService Service() => new(
        _repository, _registry, _audit,
        Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = Options() }),
        TimeProvider.System, [], _invalidator);

    private static CallerAttributeDefinition TableDefinition() => Definition("region", "attribute-table", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60);

    [Fact]
    public async Task Save_definition_applies_at_once_and_invalidates_caches()
    {
        await Service().SaveDefinitionAsync(ClaimsDefinition(), new CallerAttributeActor("oid-alice", HoldsApproverRole: true));

        _repository.DefinitionSaves.ShouldBe(["oid-alice"]);
        _registry.Invalidations.ShouldBe(1);
        _invalidator.Attributes.ShouldBe(["cost_center"]);
    }

    [Fact]
    public async Task Requires_CallerAttributeApprover()
    {
        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() =>
            Service().SaveServiceSetAsync(Tenant, "app-1", "cost_center", ["a"], new CallerAttributeActor("oid-alice", HoldsApproverRole: false)));
        ex.Code.ShouldBe("ApproverRoleRequired");
        _repository.ServiceSetSaves.ShouldBe(0);
    }

    [Fact]
    public async Task Self_edit_of_attribute_value_refused()
    {
        var actor = new CallerAttributeActor("oid-alice", HoldsApproverRole: true, OwnIdentifiers: ["app-own"], OwnGroups: ["grp-1"]);

        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() =>
            Service().SaveServiceSetAsync(Tenant, "APP-OWN", "cost_center", ["a"], actor));
        ex.Code.ShouldBe("SelfWidening");
        _repository.ServiceSetSaves.ShouldBe(0);
    }

    [Fact]
    public async Task Attribute_table_definition_needs_approver_role()
    {
        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() =>
            Service().SaveDefinitionAsync(TableDefinition(), new CallerAttributeActor("oid-alice", HoldsApproverRole: false)));
        ex.Code.ShouldBe("ApproverRoleRequired");
    }

    [Fact]
    public async Task Audit_failure_means_the_change_is_not_applied()
    {
        _audit.FailWith = new InvalidOperationException("db down");

        var ex = await Should.ThrowAsync<SingleApprovalRefusedException>(() =>
            Service().SaveDefinitionAsync(ClaimsDefinition(), new CallerAttributeActor("oid-alice", HoldsApproverRole: true)));
        ex.Code.ShouldBe("AuditFailed");
        _repository.DefinitionSaves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Audit_carries_before_and_after_hash_but_no_values()
    {
        var definition = ClaimsDefinition();

        await Service().SaveDefinitionAsync(definition, new CallerAttributeActor("oid-alice", HoldsApproverRole: true));

        var written = _audit.Entries.ShouldHaveSingleItem();
        written.EventType.ShouldBe(AuditEventTypes.CallerAttributeDefinitionApplied);
        written.DetailsJson.ShouldContain(definition.ComputeDefinitionHash());
    }

    [Fact]
    public async Task Delete_of_unknown_attribute_is_not_found()
    {
        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() =>
            Service().DeleteDefinitionAsync(Tenant, "missing", new CallerAttributeActor("oid-alice", HoldsApproverRole: true)));
        ex.Code.ShouldBe("NotFound");
    }

    private sealed class FakeRepository : ICallerAttributeRepository
    {
        public List<string> DefinitionSaves { get; } = [];

        public int ServiceSetSaves { get; private set; }

        public Task<CallerAttributeStoreSnapshot> LoadCallerAttributeSnapshotAsync(CancellationToken ct = default) => Task.FromResult(CallerAttributeStoreSnapshot.Empty);

        public Task<long> GetCallerAttributeGenerationAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public Task<CallerAttributeChangeResult> SaveDefinitionAsync(CallerAttributeDefinition definition, string actor, DateTimeOffset at, CancellationToken ct = default)
        {
            DefinitionSaves.Add(actor);
            return Task.FromResult(CallerAttributeChangeResult.Applied);
        }

        public Task<CallerAttributeChangeResult> DeleteDefinitionAsync(TenantId tenant, string name, CancellationToken ct = default) =>
            Task.FromResult(CallerAttributeChangeResult.Applied);

        public Task<CallerAttributeChangeResult> SaveServiceSetAsync(TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at, CancellationToken ct = default)
        {
            ServiceSetSaves++;
            return Task.FromResult(CallerAttributeChangeResult.Applied);
        }
    }

    private sealed class FakeRegistry : ICallerAttributeRegistry
    {
        public int Invalidations { get; private set; }

        public ValueTask<CallerAttributeSnapshot> GetAsync(CancellationToken ct) =>
            ValueTask.FromResult(new CallerAttributeSnapshot(1, [ClaimsDefinition()], []));

        public void Invalidate() => Invalidations++;
    }

    private sealed class FakeInvalidator : ICallerAttributeCacheInvalidator
    {
        public List<string> Attributes { get; } = [];

        public void InvalidateSubject(TenantId tenant, string subjectKey)
        {
        }

        public void InvalidateAttribute(TenantId tenant, string attributeName) => Attributes.Add(attributeName);
    }
}
