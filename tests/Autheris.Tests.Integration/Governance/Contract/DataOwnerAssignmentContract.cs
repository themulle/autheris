using System;
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
/// Contract suite for <c>IDataOwnerAssignmentRepository</c> across PostgreSQL and SQL Server engines. The owner tables are catalog-global
/// (no tenant column), so isolation means table scope: a run changes only the tables it lists. Every run is one transaction, never updates an
/// existing owner row, and removes only <c>dbt:</c> provenance rows in replace mode.
/// </summary>
public abstract class DataOwnerAssignmentContract : GovernanceContractBase
{
    private IDataOwnerAssignmentRepository Writer => Store.DataOwnerAssignments;
    private IDataOwnershipRepository Ownership => Store.DataOwnership;

    private static DataOwnerSpec Owner(string sid, string? name = "Owner Name", string? email = null, string? account = null, string role = "PRIMARY") =>
        new(sid, account, name, email, role);

    private DataOwnerAssignmentRequest Request(TableIdentifier t, bool replace = false, bool dryRun = false, int maxNew = 1000, params DataOwnerSpec[] owners) =>
        new([new TableOwnerAssignmentSpec(t, owners)], replace, dryRun, maxNew, TenantId: Store.TenantA);

    private DataOwnerAssignmentRequest RequestForTables(bool replace, params TableOwnerAssignmentSpec[] tables) =>
        new(tables, replace, false, TenantId: Store.TenantA);

    private Task<DataOwnerAssignmentResult> ApplyAsync(TableIdentifier t, params DataOwnerSpec[] owners) =>
        Writer.ApplyTableOwnerAssignmentsAsync(Request(t, owners: owners));

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var a = await Store.SeedTableAsync();
        var b = await Store.SeedTableAsync();
        (await ApplyAsync(a, Owner("S-1-5-21-9101", "Ann"))).Success.ShouldBeTrue();

        (await Ownership.GetDataOwnersForTableAsync(a)).Single().AdSid.ShouldBe(new Sid("S-1-5-21-9101"));
        (await Ownership.GetDataOwnersForTableAsync(b)).ShouldBeEmpty();
        (await Ownership.IsAuthorizedApproverForTableAsync(a, new Sid("S-1-5-21-9101"))).ShouldBeTrue();
        (await Ownership.IsAuthorizedApproverForTableAsync(b, new Sid("S-1-5-21-9101"))).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var a = await Store.SeedTableAsync();
        var b = await Store.SeedTableAsync();
        (await ApplyAsync(b, Owner("S-1-5-21-9102", "Bea"))).Success.ShouldBeTrue();
        var manual = Guid.NewGuid().ToString();
        await Store.SeedOwnerAsync(manual, "S-1-5-21-9103", "CORP\\cal", "Cal", "cal@corp.example");
        await Store.SeedManualAssignmentAsync(b, manual);
        (await ApplyAsync(a, Owner("S-1-5-21-9104", "Dan"))).Success.ShouldBeTrue();

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(Request(a, replace: true, owners: Owner("S-1-5-21-9105", "Eli")));

        result.Success.ShouldBeTrue();
        (await Ownership.GetDataOwnersForTableAsync(a)).Select(o => o.DisplayName).ShouldBe(["Eli"]);
        (await Ownership.GetDataOwnersForTableAsync(b)).Select(o => o.DisplayName).OrderBy(n => n).ShouldBe(["Bea", "Cal"]);
        (await Store.OwnerRowsAsync(b)).ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        var missing = new TableIdentifier("Sales", "Crm", "Nope_" + Guid.NewGuid().ToString("N")[..6]);
        var before = await Store.TotalOwnerRowsAsync();

        // The repository has no tenant parameter (the catalog is global); a missing request or an unknown table never writes anything.
        await Should.ThrowAsync<ArgumentNullException>(() => Writer.ApplyTableOwnerAssignmentsAsync(null!));
        var result = await ApplyAsync(missing, Owner("S-1-5-21-9106", "Fay"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("dbt_owner_table_not_found");
        result.ErrorTable.ShouldBe(missing.ToString());
        (await Store.TotalOwnerRowsAsync()).ShouldBe(before);
        (await Store.OwnerRowsAsync(table)).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        (await ApplyAsync(table, Owner("S-1-5-21-9002"))).Success.ShouldBeTrue();
        var version = await Store.TableMetadata.GetCatalogVersionAsync();

        var second = await ApplyAsync(table, Owner("S-1-5-21-9002"));

        second.Success.ShouldBeTrue();
        second.Changes.ShouldBeEmpty();
        (await Store.OwnerRowsAsync(table)).ShouldBe(1);
        (await Store.TableMetadata.GetCatalogVersionAsync()).ShouldBe(version);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        await Store.SeedOwnerAsync(Guid.NewGuid().ToString(), "S-1-5-21-9003", "CORP\\carl", "Carl", "carl@corp.example");
        var before = await Store.TotalOwnerRowsAsync();

        // A conflicting identity is a governed result code, never a database unique violation.
        var result = await ApplyAsync(table, Owner("S-1-5-21-9003", "Carl", "attacker@corp.example"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("dbt_owner_conflict");
        (await Store.OwnerRowsAsync(table)).ShouldBe(0);
        (await Store.TotalOwnerRowsAsync()).ShouldBe(before);
        (await Store.ExecuteScalarAsync("SELECT email FROM DATA_OWNERS WHERE ad_sid = 'S-1-5-21-9003'")).ShouldBe("carl@corp.example");

        // The same new owner listed twice in one run hits the deterministic assignment key once: no duplicate row, no violation.
        var twice = await ApplyAsync(table, Owner("S-1-5-21-9107", "Gus"), Owner("S-1-5-21-9107", "Gus"));
        twice.Success.ShouldBeTrue();
        twice.Changes.Select(c => c.Kind).ShouldBe([DataOwnerChangeKind.OwnerCreated, DataOwnerChangeKind.OwnerAssigned]);
        (await Store.OwnerRowsAsync(table)).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var a = await Store.SeedTableAsync();
        var b = await Store.SeedTableAsync();

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(RequestForTables(
            replace: false,
            new TableOwnerAssignmentSpec(a, [Owner("S-1-5-21-9201", "Ann"), Owner("S-1-5-21-9202", "Bob")]),
            new TableOwnerAssignmentSpec(b, [Owner("S-1-5-21-9203", "Cy")])));

        // Changes follow the request order: tables as listed, owners as listed, creation before assignment.
        result.Success.ShouldBeTrue();
        result.Changes.Select(c => (c.Kind, c.Table, c.OwnerSid)).ToArray().ShouldBe(
        [
            (DataOwnerChangeKind.OwnerCreated, a, "S-1-5-21-9201"),
            (DataOwnerChangeKind.OwnerAssigned, a, "S-1-5-21-9201"),
            (DataOwnerChangeKind.OwnerCreated, a, "S-1-5-21-9202"),
            (DataOwnerChangeKind.OwnerAssigned, a, "S-1-5-21-9202"),
            (DataOwnerChangeKind.OwnerCreated, b, "S-1-5-21-9203"),
            (DataOwnerChangeKind.OwnerAssigned, b, "S-1-5-21-9203")
        ]);
        (await Ownership.GetDataOwnersForTableAsync(a)).Select(o => o.DisplayName).OrderBy(n => n).ShouldBe(["Ann", "Bob"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        var before = await Store.TotalOwnerRowsAsync();

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(
            Request(table, maxNew: 1, owners: [Owner("S-1-5-21-9015", "A"), Owner("S-1-5-21-9016", "B")]));

        result.ErrorCode.ShouldBe("dbt_owner_cap_exceeded");
        (await Store.TotalOwnerRowsAsync()).ShouldBe(before);
        (await Store.OwnerRowsAsync(table)).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        var inactive = Guid.NewGuid().ToString();
        await Store.SeedOwnerAsync(inactive, "S-1-5-21-9108", "CORP\\ida", "Ida", "ida@corp.example", active: 0);
        await Store.SeedManualAssignmentAsync(table, inactive);
        (await ApplyAsync(table, Owner("S-1-5-21-9109", "Jay"))).Success.ShouldBeTrue();

        (await Ownership.GetDataOwnersForTableAsync(table)).Select(o => o.DisplayName).ShouldBe(["Jay"]);
        (await Ownership.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-9108"))).ShouldBeFalse();

        // A removed dbt assignment is invisible too, while the owner row itself stays.
        (await Writer.ApplyTableOwnerAssignmentsAsync(Request(table, replace: true))).Success.ShouldBeTrue();
        (await Ownership.GetDataOwnersForTableAsync(table)).ShouldBeEmpty();
        (await Ownership.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-9109"))).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    public async Task ApplyOwners_CreatesOwnerAndAssignment()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();

        var result = await ApplyAsync(table, Owner("S-1-5-21-9001", "Ada Owner", "ada@corp.example", "CORP\\ada"));

        result.Success.ShouldBeTrue();
        result.Changes.Select(c => c.Kind).ShouldBe([DataOwnerChangeKind.OwnerCreated, DataOwnerChangeKind.OwnerAssigned]);
        (await Ownership.GetDataOwnersForTableAsync(table)).Single().DisplayName.ShouldBe("Ada Owner");
        Convert.ToString(await Store.ExecuteScalarAsync(
            "SELECT id FROM TABLE_OWNERS WHERE table_id = @t", ("@t", await Store.TableIdAsync(table))))!.ShouldStartWith("dbt:");
        (await Ownership.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-9001"))).ShouldBeTrue();
    }

    [RequiresContainerRuntimeTheory]
    [Trait("Category", "Security")]
    [InlineData("corp\\DORA", null, null)]
    [InlineData("S-1-5-21-9099", "DORA@corp.example", null)]
    [InlineData("S-1-5-21-9099", null, "dora@CORP.example")]
    [InlineData("dora@corp.EXAMPLE", null, null)]
    public async Task Apply_IdentifierCollision_CaseVariantAcrossColumns_Rejects(string sid, string? email, string? account)
    {
        // A new owner may not share any identifier (sid, account, e-mail; any case) with another row; approver matching reads all three columns.
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        await Store.SeedOwnerAsync(Guid.NewGuid().ToString(), "S-1-5-21-9004", "CORP\\dora", "Dora", "dora@corp.example");
        var before = await Store.TotalOwnerRowsAsync();

        var result = await ApplyAsync(table, Owner(sid, "Impostor", email, account));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("dbt_owner_identifier_collision");
        (await Store.OwnerRowsAsync(table)).ShouldBe(0);
        (await Store.TotalOwnerRowsAsync()).ShouldBe(before);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Apply_IdentifierCollision_UnicodeNormalizationVariant_Rejects()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        await Store.SeedOwnerAsync(Guid.NewGuid().ToString(), "S-1-5-21-9005", "CORP\\René", "Rene", "rene@corp.example");

        var result = await ApplyAsync(table, Owner("corp\\RENÉ", "Impostor"));

        result.ErrorCode.ShouldBe("dbt_owner_identifier_collision");
    }

    [RequiresContainerRuntimeFact]
    public async Task ApplyOwners_InactiveOwner_Rejects()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        await Store.SeedOwnerAsync(Guid.NewGuid().ToString(), "S-1-5-21-9006", "CORP\\eve", "Eve", "eve@corp.example", active: 0);

        var result = await ApplyAsync(table, Owner("S-1-5-21-9006", null));

        result.ErrorCode.ShouldBe("dbt_owner_inactive");
        (await Store.OwnerRowsAsync(table)).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task ApplyOwners_MissingTable_Rejects_AndRollsBackEarlierTables()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        var missing = new TableIdentifier("Sales", "Crm", "Nope_" + Guid.NewGuid().ToString("N")[..6]);
        var before = await Store.TotalOwnerRowsAsync();

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(RequestForTables(
            replace: false,
            new TableOwnerAssignmentSpec(table, [Owner("S-1-5-21-9007")]),
            new TableOwnerAssignmentSpec(missing, [Owner("S-1-5-21-9008")])));

        result.ErrorCode.ShouldBe("dbt_owner_table_not_found");
        result.ErrorTable.ShouldBe(missing.ToString());
        (await Store.TotalOwnerRowsAsync()).ShouldBe(before);
        (await Store.OwnerRowsAsync(table)).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task ApplyOwners_ExistingManualRow_NoDuplicate()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        var ownerId = Guid.NewGuid().ToString();
        await Store.SeedOwnerAsync(ownerId, "S-1-5-21-9009", "CORP\\fay", "Fay", "fay@corp.example");
        await Store.SeedManualAssignmentAsync(table, ownerId);

        var result = await ApplyAsync(table, Owner("S-1-5-21-9009", null));

        result.Success.ShouldBeTrue();
        result.Changes.ShouldBeEmpty();
        (await Store.OwnerRowsAsync(table)).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Replace_DeletesOnlyDbtPrefixedRows()
    {
        // Manual rows survive replace; stale dbt rows go; the new owner is added.
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        var manualOwner = Guid.NewGuid().ToString();
        await Store.SeedOwnerAsync(manualOwner, "S-1-5-21-9010", "CORP\\gil", "Gil", "gil@corp.example");
        await Store.SeedManualAssignmentAsync(table, manualOwner);
        (await ApplyAsync(table, Owner("S-1-5-21-9011", "Hal"))).Success.ShouldBeTrue();
        (await Store.OwnerRowsAsync(table)).ShouldBe(2);

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(Request(table, replace: true, owners: Owner("S-1-5-21-9012", "Ivy")));

        result.Success.ShouldBeTrue();
        result.Changes.Select(c => c.Kind).ShouldBe([DataOwnerChangeKind.OwnerCreated, DataOwnerChangeKind.OwnerAssigned, DataOwnerChangeKind.OwnerRemoved]);
        result.Changes.Single(c => c.Kind == DataOwnerChangeKind.OwnerRemoved).RemovedAll.ShouldBeFalse();
        (await Ownership.GetDataOwnersForTableAsync(table)).Select(o => o.DisplayName).OrderBy(n => n).ShouldBe(["Gil", "Ivy"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Replace_EmptyOwnerList_RemovesDbtRows_FlagsRemovedAll()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        (await ApplyAsync(table, Owner("S-1-5-21-9013", "Jon"))).Success.ShouldBeTrue();

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(Request(table, replace: true));

        result.Changes.Single().Kind.ShouldBe(DataOwnerChangeKind.OwnerRemoved);
        result.Changes.Single().RemovedAll.ShouldBeTrue();
        (await Store.OwnerRowsAsync(table)).ShouldBe(0);

        // The DATA_OWNERS row itself is never deleted.
        Convert.ToInt32(await Store.ExecuteScalarAsync("SELECT COUNT(*) FROM DATA_OWNERS WHERE ad_sid = 'S-1-5-21-9013'")).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task ApplyOwners_DryRun_RollsBack_AndReportsChanges()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        var before = await Store.TotalOwnerRowsAsync();
        var version = await Store.TableMetadata.GetCatalogVersionAsync();

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(Request(table, dryRun: true, owners: Owner("S-1-5-21-9014", "Kim")));

        result.Success.ShouldBeTrue();
        result.Changes.Count.ShouldBe(2);
        (await Store.TotalOwnerRowsAsync()).ShouldBe(before);
        (await Store.OwnerRowsAsync(table)).ShouldBe(0);
        (await Store.TableMetadata.GetCatalogVersionAsync()).ShouldBe(version);
    }

    [RequiresContainerRuntimeFact]
    public async Task ApplyOwners_CreationWithoutDisplayName_Rejects()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();

        (await ApplyAsync(table, Owner("S-1-5-21-9017", null))).ErrorCode.ShouldBe("dbt_owner_display_name_required");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ApplyOwners_BareStringLookup_ResolvesExactlyOneActiveRow_NeverCreates()
    {
        if (!Store.IsAvailable) return;
        var table = await Store.SeedTableAsync();
        var before = await Store.TotalOwnerRowsAsync();
        (await ApplyAsync(table, new DataOwnerSpec("nobody@corp.example", null, null, null, "PRIMARY", LookupOnly: true))).ErrorCode.ShouldBe("dbt_owner_lookup_failed");
        (await Store.TotalOwnerRowsAsync()).ShouldBe(before);

        await Store.SeedOwnerAsync(Guid.NewGuid().ToString(), "S-1-5-21-9018", "CORP\\lee", "Lee", "lee@corp.example");
        var found = await ApplyAsync(table, new DataOwnerSpec("LEE@corp.example", null, null, null, "DELEGATE", LookupOnly: true));
        found.Success.ShouldBeTrue();
        found.Changes.Single().Role.ShouldBe("DELEGATE");
        (await Store.ExecuteScalarAsync("SELECT owner_role FROM TABLE_OWNERS WHERE table_id = @t", ("@t", await Store.TableIdAsync(table)))).ShouldBe("DELEGATE");

        // A second row sharing the e-mail makes the lookup ambiguous (legacy data).
        await Store.SeedOwnerAsync(Guid.NewGuid().ToString(), "S-1-5-21-9019", "CORP\\lee2", "Lee 2", "lee@corp.example");
        var other = await Store.SeedTableAsync();
        (await ApplyAsync(other, new DataOwnerSpec("lee@corp.example", null, null, null, "PRIMARY", LookupOnly: true))).ErrorCode.ShouldBe("dbt_owner_lookup_failed");
    }

    [RequiresContainerRuntimeFact]
    public async Task ApplyOwners_SameNewOwnerOnTwoTables_CreatesOneRow()
    {
        if (!Store.IsAvailable) return;
        var a = await Store.SeedTableAsync();
        var b = await Store.SeedTableAsync();
        var before = await Store.TotalOwnerRowsAsync();

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(RequestForTables(
            replace: false,
            new TableOwnerAssignmentSpec(a, [Owner("S-1-5-21-9020", "Max")]),
            new TableOwnerAssignmentSpec(b, [Owner("S-1-5-21-9020", "Max")])));

        result.Success.ShouldBeTrue();
        (await Store.TotalOwnerRowsAsync()).ShouldBe(before + 1);
        result.Changes.Count(c => c.Kind == DataOwnerChangeKind.OwnerCreated).ShouldBe(1);
        (await Store.OwnerRowsAsync(a)).ShouldBe(1);
        (await Store.OwnerRowsAsync(b)).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ManualOrSeedPath_NeverEmitsDbtPrefix()
    {
        // The demo seed writes TABLE_OWNERS ids that never carry the dbt: provenance prefix.
        if (!Store.IsAvailable) return;

        await using var seeded = await Store.CreateAdditionalStoreAsync(seedDemoData: true, runMigrations: true, initializeStore: true);

        Convert.ToInt32(await Store.ExecuteScalarAsync("SELECT COUNT(*) FROM TABLE_OWNERS")).ShouldBeGreaterThan(0);
        Convert.ToInt32(await Store.ExecuteScalarAsync("SELECT COUNT(*) FROM TABLE_OWNERS WHERE id LIKE 'dbt:%'")).ShouldBe(0);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlDataOwnerAssignmentContractTests(GovernancePostgresFixture fixture) : DataOwnerAssignmentContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "owner_assignment_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerDataOwnerAssignmentContractTests(GovernanceSqlServerFixture fixture) : DataOwnerAssignmentContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "owner_assignment_contract");
}
