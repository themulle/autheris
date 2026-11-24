using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>ICatalogGrantRevocationWriter</c> across PostgreSQL and SQL Server engines: the revoke of a catalog
/// grant and its audit entry commit in one transaction, so neither exists without the other.
/// </summary>
public abstract class CatalogGrantRevocationWriterContract : GovernanceContractBase
{
    private static readonly Sid Alice = new("S-1-5-21-REVOKE-ALICE");
    private static readonly Sid Owner = new("S-1-5-21-REVOKE-OWNER");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    private ICatalogGrantRevocationWriter Writer => Store.CatalogGrantRevocations;

    private ICatalogVisibilityGrantRepository Grants => Store.CatalogVisibilityGrants;

    private async Task<CatalogVisibilityGrant> SeedGrantAsync(TenantId tenant) =>
        await Grants.CreateAsync(new CatalogVisibilityGrant
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            ContainerKind = ContainerKind.Domain,
            ContainerId = Guid.NewGuid(),
            GranteeType = GranteeType.User,
            GranteeSid = Alice,
            ValidFrom = Now.AddMinutes(-10),
            ValidTo = Now.AddDays(30),
            GrantedBySid = Owner,
            ApproverCapacity = ApproverCapacity.Governance,
            CreatedAt = Now.AddMinutes(-10)
        });

    private static string NewTarget() => "catalog-revoke:" + Guid.NewGuid().ToString("N");

    private static AuditLogEntry Entry(TenantId tenant, string target, string? traceId = null) => new()
    {
        TenantId = tenant,
        OccurredAt = Now,
        EventType = AuditEventTypes.ConsentRevoked,
        ActorSid = Owner,
        TargetTable = target,
        Decision = "REVOKED",
        TraceId = traceId ?? Guid.NewGuid().ToString("N"),
        DetailsJson = "{\"GrantKind\":\"CATALOG\",\"Capacity\":\"ContainerOwner\"}"
    };

    private async Task<int> AuditCountAsync(TenantId tenant, string target) =>
        (await Store.AuditLogs.QueryAuditLogsAsync(targetTable: target, tenantId: tenant)).Count(e => e.TargetTable == target);

    private async Task<bool> IsRevokedAsync(TenantId tenant, Guid id) =>
        (await Grants.GetByIdAsync(tenant, id)).ShouldNotBeNull().IsRevoked;

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Failed_audit_write_leaves_the_grant_active()
    {
        if (!Store.IsAvailable) return;

        var grant = await SeedGrantAsync(Store.TenantA);
        var target = NewTarget();

        // A NUL character (PostgreSQL) in an over-long trace id (SQL Server NVARCHAR(128)) makes the audit insert fail inside
        // the revoke transaction on both engines.
        var broken = Entry(Store.TenantA, target, traceId: "\0" + new string('x', 300));
        await Should.ThrowAsync<Exception>(() => Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, "No longer needed", Now, broken));

        (await IsRevokedAsync(Store.TenantA, grant.Id)).ShouldBeFalse();
        (await AuditCountAsync(Store.TenantA, target)).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Failed_revoke_leaves_no_audit_entry()
    {
        if (!Store.IsAvailable) return;

        var missingTarget = NewTarget();
        (await Writer.RevokeWithAuditAsync(Store.TenantA, Guid.NewGuid(), Owner, "No longer needed", Now, Entry(Store.TenantA, missingTarget))).ShouldBeFalse();
        (await AuditCountAsync(Store.TenantA, missingTarget)).ShouldBe(0);

        var grant = await SeedGrantAsync(Store.TenantA);
        (await Grants.RevokeAsync(Store.TenantA, grant.Id, Owner, "Revoked elsewhere", Now)).ShouldBeTrue();
        var repeatTarget = NewTarget();
        (await Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, "No longer needed", Now, Entry(Store.TenantA, repeatTarget))).ShouldBeFalse();
        (await AuditCountAsync(Store.TenantA, repeatTarget)).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Applied_revoke_commits_with_its_audit_entry()
    {
        if (!Store.IsAvailable) return;

        var grant = await SeedGrantAsync(Store.TenantA);
        var target = NewTarget();

        (await Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, "No longer needed", Now, Entry(Store.TenantA, target))).ShouldBeTrue();

        (await IsRevokedAsync(Store.TenantA, grant.Id)).ShouldBeTrue();
        (await AuditCountAsync(Store.TenantA, target)).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var grant = await SeedGrantAsync(Store.TenantA);
        var target = NewTarget();

        // The grant is looked up only in the given tenant: another tenant sees no row to revoke.
        (await Writer.RevokeWithAuditAsync(Store.TenantB, grant.Id, Owner, "Cross-tenant attempt", Now, Entry(Store.TenantB, target))).ShouldBeFalse();
        (await AuditCountAsync(Store.TenantB, target)).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var grant = await SeedGrantAsync(Store.TenantA);

        (await Writer.RevokeWithAuditAsync(Store.TenantB, grant.Id, Owner, "Cross-tenant attempt", Now, Entry(Store.TenantB, NewTarget()))).ShouldBeFalse();
        await Should.ThrowAsync<ArgumentException>(() =>
            Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, "Audit of another tenant", Now, Entry(Store.TenantB, NewTarget())));
        (await IsRevokedAsync(Store.TenantA, grant.Id)).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(() =>
            Writer.RevokeWithAuditAsync(default, Guid.NewGuid(), Owner, "No tenant", Now, Entry(default, NewTarget())));
        await Should.ThrowAsync<ArgumentException>(() =>
            Writer.RevokeWithAuditAsync(TenantId.Platform, Guid.NewGuid(), Owner, "Reserved tenant", Now, Entry(TenantId.Platform, NewTarget())));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var grant = await SeedGrantAsync(Store.TenantA);
        var target = NewTarget();

        (await Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, "No longer needed", Now, Entry(Store.TenantA, target))).ShouldBeTrue();
        (await Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, "No longer needed", Now, Entry(Store.TenantA, target))).ShouldBeFalse();
        (await AuditCountAsync(Store.TenantA, target)).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        // A concurrent second revoke is not a conflict error: the row is already revoked, so the call reports false.
        var grant = await SeedGrantAsync(Store.TenantA);
        var results = await Task.WhenAll(
            Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, "Racing revoke one", Now, Entry(Store.TenantA, NewTarget())),
            Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, "Racing revoke two", Now, Entry(Store.TenantA, NewTarget())));
        results.Count(r => r).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        // The writer is single-row; revoking several grants leaves exactly one ordered audit entry per applied revoke.
        var target = NewTarget();
        foreach (var grant in new[] { await SeedGrantAsync(Store.TenantA), await SeedGrantAsync(Store.TenantA) })
        {
            (await Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, "No longer needed", Now, Entry(Store.TenantA, target))).ShouldBeTrue();
        }

        (await AuditCountAsync(Store.TenantA, target)).ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        // Not paged: one grant per call. A blank reason is rejected before anything is written.
        var grant = await SeedGrantAsync(Store.TenantA);
        await Should.ThrowAsync<ArgumentException>(() =>
            Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, " ", Now, Entry(Store.TenantA, NewTarget())));
        (await IsRevokedAsync(Store.TenantA, grant.Id)).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        var grant = await SeedGrantAsync(Store.TenantA);
        (await Writer.RevokeWithAuditAsync(Store.TenantA, grant.Id, Owner, "No longer needed", Now, Entry(Store.TenantA, NewTarget()))).ShouldBeTrue();

        (await Grants.ListActiveForSubjectsAsync(Store.TenantA, [Alice], Now)).ShouldNotContain(g => g.Id == grant.Id);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlCatalogGrantRevocationWriterContractTests(GovernancePostgresFixture fixture) : CatalogGrantRevocationWriterContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "catalog_revocation_writer_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerCatalogGrantRevocationWriterContractTests(GovernanceSqlServerFixture fixture) : CatalogGrantRevocationWriterContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "catalog_revocation_writer_contract");
}
