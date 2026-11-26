using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>ITableWritePolicyRepository</c> across PostgreSQL and SQL Server engines (issue #1154):
/// tenant isolation, idempotent upsert, delete visibility, and schema step 0049 persistence round-trip.
/// </summary>
public abstract class TableWritePolicyContract : GovernanceContractBase
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-26T00:00:00Z");

    private ITableWritePolicyRepository Repo => Store.TableWritePolicies;

    private static TableWritePolicy NewPolicy(TenantId tenant, Guid tableId, long? maxRows = 100) => new(
        Tenant: tenant,
        TableId: tableId,
        Mode: TableWriteMode.BoundedSingleTable,
        AllowedOperations: [WriteOperation.Insert, WriteOperation.Update, WriteOperation.Delete],
        WritableColumns: ["param_id", "department_id", "param_value"],
        RequirePrimaryKeyPredicate: true,
        RequireRowFilter: true,
        MaxRowsPerStatement: maxRows,
        ServerStampedColumns: new Dictionary<string, string> { ["updated_by_sid"] = TableWriteServerStampKinds.CallerSid },
        UpdatedBy: "S-1-5-21-GOVADMIN",
        UpdatedAt: Now);

    [RequiresContainerRuntimeFact]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var tableId = Guid.NewGuid();
        await Repo.UpsertAsync(NewPolicy(Store.TenantA, tableId));

        (await Repo.GetAsync(Store.TenantA, tableId)).ShouldNotBeNull();
        (await Repo.GetAsync(Store.TenantB, tableId)).ShouldBeNull();
        (await Repo.ListAsync(Store.TenantA)).ShouldContain(p => p.TableId == tableId);
        (await Repo.ListAsync(Store.TenantB)).ShouldNotContain(p => p.TableId == tableId);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var tableId = Guid.NewGuid();
        await Repo.UpsertAsync(NewPolicy(Store.TenantA, tableId, maxRows: 25));

        (await Repo.DeleteAsync(Store.TenantB, tableId)).ShouldBeFalse();
        var intact = await Repo.GetAsync(Store.TenantA, tableId);
        intact.ShouldNotBeNull();
        intact!.MaxRowsPerStatement.ShouldBe(25);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(() => Repo.ListAsync(default));
        await Should.ThrowAsync<ArgumentException>(() => Repo.ListAsync(TenantId.Platform));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetAsync(default, Guid.NewGuid()));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var tableId = Guid.NewGuid();
        var initial = NewPolicy(Store.TenantA, tableId, maxRows: 50);
        await Repo.UpsertAsync(initial);
        await Repo.UpsertAsync(initial with { MaxRowsPerStatement = 15, RequireRowFilter = false });

        var stored = await Repo.GetAsync(Store.TenantA, tableId);
        stored.ShouldNotBeNull();
        stored!.MaxRowsPerStatement.ShouldBe(15);
        stored.RequireRowFilter.ShouldBeFalse();
        stored.AllowedOperations.ShouldBe([WriteOperation.Insert, WriteOperation.Update, WriteOperation.Delete]);
        stored.ServerStampedColumns.ShouldNotBeNull();
        stored.ServerStampedColumns!["updated_by_sid"].ShouldBe(TableWriteServerStampKinds.CallerSid);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        // Primary key (tenant_id, table_id) is per tenant: two tenants may store independent policies for the same table id.
        var tableId = Guid.NewGuid();
        await Repo.UpsertAsync(NewPolicy(Store.TenantA, tableId, maxRows: 10));
        await Repo.UpsertAsync(NewPolicy(Store.TenantB, tableId, maxRows: 20));

        (await Repo.GetAsync(Store.TenantA, tableId))!.MaxRowsPerStatement.ShouldBe(10);
        (await Repo.GetAsync(Store.TenantB, tableId))!.MaxRowsPerStatement.ShouldBe(20);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        var id1 = Guid.Parse("00000001-0000-0000-0000-000000000001");
        var id2 = Guid.Parse("00000002-0000-0000-0000-000000000002");
        await Repo.UpsertAsync(NewPolicy(Store.TenantA, id2));
        await Repo.UpsertAsync(NewPolicy(Store.TenantA, id1));

        var listed = (await Repo.ListAsync(Store.TenantA))
            .Where(p => p.TableId == id1 || p.TableId == id2)
            .Select(p => p.TableId)
            .ToArray();
        listed.ShouldBe([id1, id2]);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        // Invalid policy parameters (non-positive MaxRowsPerStatement) are rejected fail-closed.
        var bad = NewPolicy(Store.TenantA, Guid.NewGuid(), maxRows: 0);
        await Should.ThrowAsync<InvalidOperationException>(() => Repo.UpsertAsync(bad));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        var tableId = Guid.NewGuid();
        await Repo.UpsertAsync(NewPolicy(Store.TenantA, tableId));
        (await Repo.DeleteAsync(Store.TenantA, tableId)).ShouldBeTrue();
        (await Repo.DeleteAsync(Store.TenantA, tableId)).ShouldBeFalse();
        (await Repo.GetAsync(Store.TenantA, tableId)).ShouldBeNull();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlTableWritePolicyContractTests(GovernancePostgresFixture fixture) : TableWritePolicyContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "table_write_policy_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerTableWritePolicyContractTests(GovernanceSqlServerFixture fixture) : TableWritePolicyContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "table_write_policy_contract");
}
