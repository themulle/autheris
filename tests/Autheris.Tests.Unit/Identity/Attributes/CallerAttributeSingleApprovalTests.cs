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

    private static CallerAttributeActor Actor(bool approver) => new("oid-alice", approver, ["alice@example.test"], ["grp-alice"]);

    private static CallerAttributeDefinition TableDefinition() => Definition("region", "attribute-table", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60);

    [Fact]
    public async Task Save_definition_applies_at_once_and_invalidates_caches()
    {
        await Service().SaveDefinitionAsync(ClaimsDefinition(), Actor(true));

        _repository.DefinitionSaves.ShouldBe(["oid-alice"]);
        _registry.Invalidations.ShouldBe(1);
        _invalidator.Attributes.ShouldBe(["cost_center"]);
    }

    [Fact]
    public async Task Requires_CallerAttributeApprover()
    {
        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() =>
            Service().SaveServiceSetAsync(Tenant, "app-1", "cost_center", ["a"], Actor(false)));
        ex.Code.ShouldBe("ApproverRoleRequired");
        _repository.ServiceSetSaves.ShouldBe(0);
    }

    [Fact]
    public async Task Self_edit_of_attribute_value_refused()
    {
        var actor = new CallerAttributeActor("oid-alice", holdsApproverRole: true, ownIdentifiers: ["app-own"], ownGroups: ["grp-1"]);

        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() =>
            Service().SaveServiceSetAsync(Tenant, "APP-OWN", "cost_center", ["a"], actor));
        ex.Code.ShouldBe("SelfWidening");
        _repository.ServiceSetSaves.ShouldBe(0);
    }

    [Fact]
    public async Task Attribute_table_definition_needs_approver_role()
    {
        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() =>
            Service().SaveDefinitionAsync(TableDefinition(), Actor(false)));
        ex.Code.ShouldBe("ApproverRoleRequired");
    }

    [Fact]
    public async Task Audit_failure_means_the_change_is_not_applied()
    {
        _repository.FailAudit = true;

        await Should.ThrowAsync<InvalidOperationException>(() => Service().SaveDefinitionAsync(ClaimsDefinition(), Actor(true)));

        _repository.DefinitionSaves.ShouldBeEmpty("the audit is written in the transaction of the write; its failure rolls the write back");
        _repository.Audits.ShouldBeEmpty();
        _registry.Invalidations.ShouldBe(0);
    }

    [Fact]
    public async Task Failed_write_leaves_no_audit_entry()
    {
        _repository.DeleteResult = CallerAttributeChangeResult.NotFound;
        _repository.Snapshot = new CallerAttributeStoreSnapshot(1, [new CallerAttributeRecord(ClaimsDefinition(), "oid-bob", "oid-bob", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)], []);

        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() => Service().DeleteDefinitionAsync(Tenant, "cost_center", Actor(true)));

        ex.Code.ShouldBe("NotFound");
        _repository.Audits.ShouldBeEmpty("an entry is written only together with an applied write");
        _audit.Entries.ShouldBeEmpty("nothing is audited outside the transaction of the write");
    }

    [Fact]
    public async Task Audit_carries_before_and_after_hash_but_no_values()
    {
        var definition = ClaimsDefinition();

        await Service().SaveDefinitionAsync(definition, Actor(true));

        var written = _repository.Audits.ShouldHaveSingleItem();
        written.EventType.ShouldBe(AuditEventTypes.CallerAttributeDefinitionApplied);
        written.DetailsJson.ShouldContain(definition.ComputeDefinitionHash());
        _audit.Entries.ShouldBeEmpty("the entry is written with the change, never separately before it");
    }

    [Fact]
    public void Actor_requires_its_own_identifiers_and_groups()
    {
        Should.Throw<ArgumentNullException>(() => new CallerAttributeActor("oid-alice", true, null!, []));
        Should.Throw<ArgumentNullException>(() => new CallerAttributeActor("oid-alice", true, [], null!));
        Should.Throw<ArgumentException>(() => new CallerAttributeActor(" ", true, [], []));
        new CallerAttributeActor("oid-alice", true, [], []).OwnIdentifiers.ShouldContain("oid-alice", "the subject key is always an own identifier");
    }

    [Fact]
    public async Task Self_edit_through_the_subject_key_is_refused_even_without_listed_identifiers()
    {
        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() =>
            Service().SaveServiceSetAsync(Tenant, "oid-alice", "cost_center", ["a"], new CallerAttributeActor("oid-alice", true, [], [])));

        ex.Code.ShouldBe("SelfWidening");
        _repository.ServiceSetSaves.ShouldBe(0);
    }

    [Fact]
    public async Task Delete_of_unknown_attribute_is_not_found()
    {
        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() =>
            Service().DeleteDefinitionAsync(Tenant, "missing", Actor(true)));
        ex.Code.ShouldBe("NotFound");
    }

    private sealed class FakeRepository : ICallerAttributeRepository
    {
        public List<string> DefinitionSaves { get; } = [];

        public List<AuditLogEntry> Audits { get; } = [];

        public int ServiceSetSaves { get; private set; }

        public bool FailAudit { get; set; }

        public CallerAttributeChangeResult DeleteResult { get; set; } = CallerAttributeChangeResult.Applied;

        public CallerAttributeStoreSnapshot Snapshot { get; set; } = CallerAttributeStoreSnapshot.Empty;

        public Task<CallerAttributeStoreSnapshot> LoadCallerAttributeSnapshotAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);

        public Task<long> GetCallerAttributeGenerationAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public Task<CallerAttributeChangeResult> SaveDefinitionAsync(CallerAttributeDefinition definition, string actor, DateTimeOffset at, AuditLogEntry? audit = null, CancellationToken ct = default) =>
            Commit(CallerAttributeChangeResult.Applied, audit, () => DefinitionSaves.Add(actor));

        public Task<CallerAttributeChangeResult> DeleteDefinitionAsync(TenantId tenant, string name, AuditLogEntry? audit = null, CancellationToken ct = default) =>
            Commit(DeleteResult, audit, () => { });

        public Task<CallerAttributeChangeResult> SaveServiceSetAsync(TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at, AuditLogEntry? audit = null, CancellationToken ct = default) =>
            Commit(CallerAttributeChangeResult.Applied, audit, () => ServiceSetSaves++);

        /// <summary>Models the store transaction: the write and its audit entry commit together or not at all.</summary>
        private Task<CallerAttributeChangeResult> Commit(CallerAttributeChangeResult result, AuditLogEntry? audit, Action apply)
        {
            if (result != CallerAttributeChangeResult.Applied)
            {
                return Task.FromResult(result);
            }

            if (FailAudit)
            {
                throw new InvalidOperationException("audit store down");
            }

            apply();
            if (audit is not null)
            {
                Audits.Add(audit);
            }

            return Task.FromResult(result);
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
