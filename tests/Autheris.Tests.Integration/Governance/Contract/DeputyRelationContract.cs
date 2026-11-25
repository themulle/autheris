using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Governance.Ownership;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>IDeputyRelationRepository</c> across PostgreSQL and SQL Server engines (issue #1126): tenant scoping, the
/// filter semantics the deputy seam relies on, compare-and-set change sets and atomicity with the audit entries.
/// </summary>
public abstract class DeputyRelationContract : GovernanceContractBase
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T10:00:00Z");

    private IDeputyRelationRepository Repo => Store.DeputyRelations;

    private static DeputyRelation NewRelation(TenantId tenant, string externalId, string deputy = "alice", string principal = "bob", string source = "hr") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant,
        Source = source,
        ExternalId = externalId,
        PrincipalRef = "corp|" + principal,
        PrincipalKey = DeputyKey.Qualified("corp", principal),
        PrincipalAltKey = DeputyKey.Qualified("corp", "S-1-5-21-" + principal),
        DeputyRef = "corp|" + deputy,
        DeputyKey = DeputyKey.Qualified("corp", deputy),
        DeputyAltKey = null,
        ValidFrom = Now.AddDays(-1),
        ValidTo = Now.AddDays(5),
        CreatedBy = new Sid("hr-sync"),
        CreatedAt = Now
    };

    private static AuditLogEntry Audit(TenantId tenant, string target, string? traceId = null) => new()
    {
        TenantId = tenant,
        OccurredAt = Now,
        EventType = AuditEventTypes.DeputyRelationCreated,
        ActorSid = new Sid("hr-sync"),
        TargetTable = target,
        Decision = "CREATED",
        TraceId = traceId ?? Guid.NewGuid().ToString("N"),
        DetailsJson = "{}"
    };

    private Task<bool> InsertAsync(TenantId tenant, params DeputyRelation[] rows) =>
        Repo.ApplyAsync(tenant, new DeputyRelationChanges(rows, [], []), rows.Select(r => Audit(tenant, "deputy:" + r.ExternalId)).ToList());

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var relation = NewRelation(Store.TenantA, "r-" + Guid.NewGuid().ToString("N"));
        (await InsertAsync(Store.TenantA, relation)).ShouldBeTrue();

        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: "hr"), 100)).ShouldContain(r => r.Id == relation.Id);
        (await Repo.ListAsync(Store.TenantB, new DeputyRelationFilter(Source: "hr"), 100)).ShouldNotContain(r => r.Id == relation.Id);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var relation = NewRelation(Store.TenantA, "w-" + Guid.NewGuid().ToString("N"));
        await InsertAsync(Store.TenantA, relation);
        var foreign = relation with { TenantId = Store.TenantB };

        (await Repo.ApplyAsync(Store.TenantB, new DeputyRelationChanges([], [], [foreign]), [])).ShouldBeFalse();
        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: "hr"), 100)).ShouldContain(r => r.Id == relation.Id);
        await Should.ThrowAsync<ArgumentException>(() => Repo.ApplyAsync(Store.TenantA, new DeputyRelationChanges([foreign], [], []), []));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(() => Repo.ListAsync(default, new DeputyRelationFilter(), 10));
        await Should.ThrowAsync<ArgumentException>(() => Repo.ListAsync(TenantId.Platform, new DeputyRelationFilter(), 10));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        // Updates are compare-and-set on the "before" state: replaying a stale update changes nothing.
        var before = NewRelation(Store.TenantA, "u-" + Guid.NewGuid().ToString("N"));
        await InsertAsync(Store.TenantA, before);
        var stored = (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: "hr"), 1000)).Single(r => r.Id == before.Id);
        var after = stored with { ValidTo = Now.AddDays(9), UpdatedBy = new Sid("hr-sync"), UpdatedAt = Now };

        (await Repo.ApplyAsync(Store.TenantA, new DeputyRelationChanges([], [(stored, after)], []), [Audit(Store.TenantA, "deputy:u")])).ShouldBeTrue();
        (await Repo.ApplyAsync(Store.TenantA, new DeputyRelationChanges([], [(stored, after)], []), [Audit(Store.TenantA, "deputy:u")])).ShouldBeFalse();
        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: "hr"), 1000)).Single(r => r.Id == before.Id).ValidTo.ShouldBe(Now.AddDays(9));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var externalId = "dup-" + Guid.NewGuid().ToString("N");
        (await InsertAsync(Store.TenantA, NewRelation(Store.TenantA, externalId))).ShouldBeTrue();
        (await InsertAsync(Store.TenantA, NewRelation(Store.TenantA, externalId, deputy: "carol"))).ShouldBeFalse();
        (await InsertAsync(Store.TenantB, NewRelation(Store.TenantB, externalId))).ShouldBeTrue("the external id is unique per tenant and source");
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        var source = "order" + Guid.NewGuid().ToString("N")[..8];
        await InsertAsync(Store.TenantA, NewRelation(Store.TenantA, "b", source: source), NewRelation(Store.TenantA, "a", source: source));

        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source), 10)).Select(r => r.ExternalId).ShouldBe(["a", "b"]);
        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source), 1)).ShouldHaveSingleItem().ExternalId.ShouldBe("a");
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(), 0));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        // An ended relation is deleted (its audit entry keeps the before state); expired rows are filtered by the active window.
        var source = "end" + Guid.NewGuid().ToString("N")[..8];
        var ended = NewRelation(Store.TenantA, "ended", source: source);
        var expired = NewRelation(Store.TenantA, "expired", source: source) with { ValidTo = Now.AddMinutes(-1) };
        await InsertAsync(Store.TenantA, ended, expired);
        var stored = (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source), 10)).Single(r => r.ExternalId == "ended");

        (await Repo.ApplyAsync(Store.TenantA, new DeputyRelationChanges([], [], [stored]), [Audit(Store.TenantA, "deputy:ended")])).ShouldBeTrue();
        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source), 10)).ShouldHaveSingleItem().ExternalId.ShouldBe("expired");
        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source, ActiveAt: Now), 10)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Key_filters_match_deputy_principal_and_alternative_keys()
    {
        if (!Store.IsAvailable) return;

        var source = "keys" + Guid.NewGuid().ToString("N")[..8];
        var relation = NewRelation(Store.TenantA, "k", deputy: "dora", principal: "paul", source: source);
        await InsertAsync(Store.TenantA, relation);

        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source, DeputyKeys: [DeputyKey.Qualified("corp", "dora")], ActiveAt: Now), 10)).ShouldHaveSingleItem();
        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source, DeputyKeys: [DeputyKey.Qualified("corp", "paul")]), 10)).ShouldBeEmpty();
        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source, PrincipalKeys: [DeputyKey.Qualified("corp", "S-1-5-21-paul")]), 10)).ShouldHaveSingleItem();
        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source, SubjectKeys: [DeputyKey.Qualified("corp", "paul")]), 10)).ShouldHaveSingleItem();
        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source, ActiveAt: Now.AddDays(6)), 10)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Failed_audit_write_rolls_the_whole_change_set_back()
    {
        if (!Store.IsAvailable) return;

        var source = "atomic" + Guid.NewGuid().ToString("N")[..8];
        var kept = NewRelation(Store.TenantA, "kept", source: source);
        await InsertAsync(Store.TenantA, kept);
        var stored = (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source), 10)).Single();

        // A NUL character (PostgreSQL) in an over-long trace id (SQL Server NVARCHAR(128)) makes the audit insert fail on both engines.
        var broken = Audit(Store.TenantA, "deputy:broken", traceId: "\0" + new string('x', 300));
        await Should.ThrowAsync<Exception>(() => Repo.ApplyAsync(
            Store.TenantA, new DeputyRelationChanges([NewRelation(Store.TenantA, "new", source: source)], [], [stored]), [broken]));

        (await Repo.ListAsync(Store.TenantA, new DeputyRelationFilter(Source: source), 10)).ShouldHaveSingleItem().ExternalId.ShouldBe("kept");
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlDeputyRelationContractTests(GovernancePostgresFixture fixture) : DeputyRelationContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "deputy_relation_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerDeputyRelationContractTests(GovernanceSqlServerFixture fixture) : DeputyRelationContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "deputy_relation_contract");
}
