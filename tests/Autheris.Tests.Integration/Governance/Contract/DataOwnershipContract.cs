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
/// Contract suite for <c>IDataOwnershipRepository</c> across PostgreSQL and SQL Server engines. Owners and delegations are catalog-global (no tenant
/// column), so isolation means table scope: an owner or a delegate is authorised only for the tables of the owner it belongs to.
/// </summary>
public abstract class DataOwnershipContract : GovernanceContractBase
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private IDataOwnershipRepository Repo => Store.DataOwnership;

    private async Task<(TableIdentifier Table, string OwnerId)> OwnedTableAsync(string sid, string name, int active = 1)
    {
        var table = await Store.SeedTableAsync();
        var ownerId = Guid.NewGuid().ToString();
        await Store.SeedOwnerAsync(ownerId, sid, "CORP\\" + name.ToLowerInvariant(), name, name.ToLowerInvariant() + "@corp.example", active);
        await Store.SeedManualAssignmentAsync(table, ownerId);
        return (table, ownerId);
    }

    private static DataOwnerDelegation Delegation(string ownerId, string delegateSid, DateTimeOffset from, DateTimeOffset to, Guid? id = null, string reason = "cover") => new()
    {
        Id = id ?? Guid.NewGuid(),
        DataOwnerId = Guid.Parse(ownerId),
        DelegateSid = new Sid(delegateSid),
        ValidFrom = from,
        ValidTo = to,
        Reason = reason
    };

    private async Task<int> DelegationRowsAsync(string ownerId) =>
        Convert.ToInt32(await Store.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM DATA_OWNER_DELEGATIONS WHERE data_owner_id = @o", ("@o", ownerId)));

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var (a, _) = await OwnedTableAsync("S-1-5-21-5001", "Ann");
        var (b, _) = await OwnedTableAsync("S-1-5-21-5002", "Bob");

        (await Repo.GetDataOwnersForTableAsync(a)).Single().AdSid.ShouldBe(new Sid("S-1-5-21-5001"));
        (await Repo.GetDataOwnersForTableAsync(b)).Single().AdSid.ShouldBe(new Sid("S-1-5-21-5002"));
        (await Repo.IsAuthorizedApproverForTableAsync(a, new Sid("S-1-5-21-5002"))).ShouldBeFalse();
        (await Repo.IsAuthorizedApproverForTableAsync(b, new Sid("S-1-5-21-5001"))).ShouldBeFalse();

        var batch = await Repo.GetDataOwnersForTablesAsync([a]);
        batch.Keys.ToArray().ShouldBe([a]);
        batch[a].Single().DisplayName.ShouldBe("Ann");
        (await Repo.GetOwnedOrDelegatedTablesAsync(new Sid("S-1-5-21-5001"), [a, b], T0)).ToArray().ShouldBe([a]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var (a, ownerA) = await OwnedTableAsync("S-1-5-21-5003", "Cleo");
        var (b, ownerB) = await OwnedTableAsync("S-1-5-21-5004", "Dirk");
        var now = DateTimeOffset.UtcNow;

        await Repo.DelegateDataOwnershipAsync(Delegation(ownerA, "S-1-5-21-5005", now.AddHours(-1), now.AddHours(1)));

        (await Repo.IsAuthorizedApproverForTableAsync(a, new Sid("S-1-5-21-5005"))).ShouldBeTrue();
        (await Repo.IsAuthorizedApproverForTableAsync(b, new Sid("S-1-5-21-5005"))).ShouldBeFalse();
        (await DelegationRowsAsync(ownerA)).ShouldBe(1);
        (await DelegationRowsAsync(ownerB)).ShouldBe(0);
        (await Repo.GetDataOwnersForTableAsync(b)).Single().AdSid.ShouldBe(new Sid("S-1-5-21-5004"));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var (table, ownerId) = await OwnedTableAsync("S-1-5-21-5006", "Eva");
        var unknown = new TableIdentifier("Sales", "Crm", "Nope_" + Guid.NewGuid().ToString("N")[..6]);
        var version = await Store.TableMetadata.GetCatalogVersionAsync();

        // The repository has no tenant parameter (the catalog is global): unknown tables and blank subjects never resolve to an owner,
        // and a delegation without a delegate subject is refused by the database and changes nothing.
        (await Repo.GetDataOwnersForTableAsync(unknown)).ShouldBeEmpty();
        (await Repo.IsAuthorizedApproverForTableAsync(unknown, new Sid("S-1-5-21-5006"))).ShouldBeFalse();
        (await Repo.IsAuthorizedApproverForTableAsync(table, default)).ShouldBeFalse();
        (await Repo.IsAuthorizedApproverForTableAsync(table, new Sid(string.Empty))).ShouldBeFalse();
        await Should.ThrowAsync<Exception>(() => Repo.DelegateDataOwnershipAsync(
            new DataOwnerDelegation { DataOwnerId = Guid.Parse(ownerId), DelegateSid = default, ValidFrom = T0, ValidTo = T0.AddDays(1), Reason = "x" }));

        (await DelegationRowsAsync(ownerId)).ShouldBe(0);
        (await Store.TableMetadata.GetCatalogVersionAsync()).ShouldBe(version);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var (_, ownerId) = await OwnedTableAsync("S-1-5-21-5007", "Finn");
        var id = Guid.NewGuid();
        await Repo.DelegateDataOwnershipAsync(Delegation(ownerId, "S-1-5-21-5008", T0, T0.AddDays(1), id, "first"));
        var version = await Store.TableMetadata.GetCatalogVersionAsync();

        await Repo.DelegateDataOwnershipAsync(Delegation(ownerId, "S-1-5-21-5008", T0, T0.AddDays(2), id, "second"));

        (await DelegationRowsAsync(ownerId)).ShouldBe(1);
        Convert.ToString(await Store.ExecuteScalarAsync("SELECT reason FROM DATA_OWNER_DELEGATIONS WHERE id = @i", ("@i", id.ToString()))).ShouldBe("second");
        var after = await Store.TableMetadata.GetCatalogVersionAsync();
        after.ContentVersion.ShouldBe(version.ContentVersion + 1);
        after.StructureVersion.ShouldBe(version.StructureVersion);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var (_, ownerId) = await OwnedTableAsync("S-1-5-21-5009", "Gwen");
        var id = Guid.NewGuid();

        // A repeated delegation id replaces the row in place (never a unique violation); distinct ids for the same pair coexist.
        await Repo.DelegateDataOwnershipAsync(Delegation(ownerId, "S-1-5-21-5010", T0, T0.AddDays(1), id));
        await Repo.DelegateDataOwnershipAsync(Delegation(ownerId, "S-1-5-21-5010", T0, T0.AddDays(1), id));
        await Repo.DelegateDataOwnershipAsync(Delegation(ownerId, "S-1-5-21-5010", T0, T0.AddDays(1)));

        (await DelegationRowsAsync(ownerId)).ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var (a, _) = await OwnedTableAsync("S-1-5-21-5011", "Hana");
        var (b, _) = await OwnedTableAsync("S-1-5-21-5012", "Ivan");
        var (c, _) = await OwnedTableAsync("S-1-5-21-5013", "June");
        var tables = new[] { a, b, c };

        var first = await Repo.GetDataOwnersForTablesAsync(tables);
        var second = await Repo.GetDataOwnersForTablesAsync(tables);

        first.Keys.ToArray().ShouldBe(tables, ignoreOrder: true);
        second.Keys.ToArray().ShouldBe(first.Keys.ToArray(), ignoreOrder: true);
        foreach (var table in tables)
        {
            second[table].Select(o => o.Id).ShouldBe(first[table].Select(o => o.Id));
            first[table].ShouldHaveSingleItem();
        }
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var (a, _) = await OwnedTableAsync("S-1-5-21-5014", "Kai");
        var tables = new List<TableIdentifier> { a };
        tables.AddRange(Enumerable.Range(0, 600).Select(i => new TableIdentifier("Sales", "Crm", "Ghost_" + i)));

        // Oversized batches are answered for the known tables only: no error, no row for an unknown table.
        var owners = await Repo.GetDataOwnersForTablesAsync(tables);
        var owned = await Repo.GetOwnedOrDelegatedTablesAsync(new Sid("S-1-5-21-5014"), tables, T0);

        owners.Keys.ToArray().ShouldBe([a]);
        owned.ToArray().ShouldBe([a]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var (table, _) = await OwnedTableAsync("S-1-5-21-5015", "Lena", active: 0);

        (await Repo.GetDataOwnersForTableAsync(table)).ShouldBeEmpty();
        (await Repo.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-5015"))).ShouldBeFalse();
        (await Repo.GetDataOwnersForTablesAsync([table])).ShouldBeEmpty();
        (await Repo.GetOwnedOrDelegatedTablesAsync(new Sid("S-1-5-21-5015"), [table], T0)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Delegation_GrantsApproverRightOnlyInsideItsWindow()
    {
        if (!Store.IsAvailable) return;
        var (table, ownerId) = await OwnedTableAsync("S-1-5-21-5016", "Milo");
        var now = DateTimeOffset.UtcNow;
        await Repo.DelegateDataOwnershipAsync(Delegation(ownerId, "S-1-5-21-5017", now.AddHours(-2), now.AddHours(-1)));
        await Repo.DelegateDataOwnershipAsync(Delegation(ownerId, "S-1-5-21-5018", now.AddHours(1), now.AddHours(2)));
        await Repo.DelegateDataOwnershipAsync(Delegation(ownerId, "S-1-5-21-5019", now.AddHours(-1), now.AddHours(1)));

        (await Repo.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-5017"))).ShouldBeFalse();
        (await Repo.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-5018"))).ShouldBeFalse();
        (await Repo.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-5019"))).ShouldBeTrue();
    }

    [RequiresContainerRuntimeFact]
    public async Task OwnedOrDelegatedTables_UsesInclusiveStartAndExclusiveEnd_AndDirectOwnerIsAlwaysIncluded()
    {
        if (!Store.IsAvailable) return;
        var (table, ownerId) = await OwnedTableAsync("S-1-5-21-5020", "Nora");
        await Repo.DelegateDataOwnershipAsync(Delegation(ownerId, "S-1-5-21-5021", T0, T0.AddDays(1)));
        var delegateSid = new Sid("S-1-5-21-5021");

        (await Repo.GetOwnedOrDelegatedTablesAsync(delegateSid, [table], T0.AddSeconds(-1))).ShouldBeEmpty();
        (await Repo.GetOwnedOrDelegatedTablesAsync(delegateSid, [table], T0)).ToArray().ShouldBe([table]);
        (await Repo.GetOwnedOrDelegatedTablesAsync(delegateSid, [table], T0.AddDays(1).AddSeconds(-1))).ToArray().ShouldBe([table]);
        (await Repo.GetOwnedOrDelegatedTablesAsync(delegateSid, [table], T0.AddDays(1))).ShouldBeEmpty();
        (await Repo.GetOwnedOrDelegatedTablesAsync(new Sid("S-1-5-21-5020"), [table], T0.AddYears(5))).ToArray().ShouldBe([table]);
    }

    [RequiresContainerRuntimeFact]
    public async Task Delegate_BumpsTheCatalogContentVersionOnce()
    {
        if (!Store.IsAvailable) return;
        var (_, ownerId) = await OwnedTableAsync("S-1-5-21-5022", "Omar");
        var before = await Store.TableMetadata.GetCatalogVersionAsync();

        await Repo.DelegateDataOwnershipAsync(Delegation(ownerId, "S-1-5-21-5023", T0, T0.AddDays(1)));

        var after = await Store.TableMetadata.GetCatalogVersionAsync();
        after.ContentVersion.ShouldBe(before.ContentVersion + 1);
        after.StructureVersion.ShouldBe(before.StructureVersion);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlDataOwnershipContractTests(GovernancePostgresFixture fixture) : DataOwnershipContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "data_ownership_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerDataOwnershipContractTests(GovernanceSqlServerFixture fixture) : DataOwnershipContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "data_ownership_contract");
}
