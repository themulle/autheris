using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Governance;

/// <summary>
/// Issue #393 part b (I393B-SC-05..09, T-07, T-09): persistence contract of <see cref="IDataOwnerAssignmentRepository"/>; run on SQLite (unit) and
/// on PostgreSQL and SQL Server (integration, linked). Every test seeds its own table; one database serves one test class instance.
/// </summary>
public abstract class DataOwnerAssignmentRepositoryContractTestsBase : IAsyncLifetime
{
    protected IGovernanceRepository Repo { get; private set; } = null!;
    protected bool Available { get; private set; }

    protected abstract Task<IGovernanceRepository?> CreateRepositoryAsync();

    protected abstract Task DisposeRepositoryAsync();

    protected abstract DbConnection OpenRaw();

    public async Task InitializeAsync()
    {
        var repo = await CreateRepositoryAsync();
        Available = repo != null;
        if (repo != null)
        {
            Repo = repo;
        }
    }

    public Task DisposeAsync() => DisposeRepositoryAsync();

    private IDataOwnerAssignmentRepository Writer => (IDataOwnerAssignmentRepository)Repo;

    protected async Task<TableIdentifier> SeedTableAsync()
    {
        var id = new TableIdentifier("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table { Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, Sensitivity = "LOW", RequiresFourEyes = false, IsActive = true };
        await Repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id, Table = table, Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        });
        return id;
    }

    protected object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private int Count(string sql, params (string Name, object Value)[] parameters) => Convert.ToInt32(Scalar(sql, parameters));

    private string TableId(TableIdentifier t) => Scalar(
        "SELECT id FROM TABLES WHERE source_name = @d AND schema_name = @s AND table_name = @t", ("@d", t.Domain), ("@s", t.Schema), ("@t", t.TableName))!.ToString()!;

    private int OwnerRows(TableIdentifier t) => Count("SELECT COUNT(*) FROM TABLE_OWNERS WHERE table_id = @t", ("@t", TableId(t)));

    private int TotalOwnerRows() => Count("SELECT COUNT(*) FROM DATA_OWNERS");

    protected void SeedOwner(string id, string sid, string account, string name, string email, int active = 1) => Scalar(
        "INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active) VALUES (@id, @sid, @acc, @n, @e, @a); SELECT 1",
        ("@id", id), ("@sid", sid), ("@acc", account), ("@n", name), ("@e", email), ("@a", active));

    protected void SeedManualAssignment(TableIdentifier t, string ownerId, string role = "PRIMARY") => Scalar(
        "INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role) VALUES (@id, @t, @o, @r); SELECT 1",
        ("@id", Guid.NewGuid().ToString()), ("@t", TableId(t)), ("@o", ownerId), ("@r", role));

    private static DataOwnerSpec Owner(string sid, string? name = "Owner Name", string? email = null, string? account = null, string role = "PRIMARY") =>
        new(sid, account, name, email, role);

    private static DataOwnerAssignmentRequest Request(TableIdentifier t, bool replace = false, bool dryRun = false, int maxNew = 1000, params DataOwnerSpec[] owners) =>
        new([new TableOwnerAssignmentSpec(t, owners)], replace, dryRun, maxNew);

    private Task<DataOwnerAssignmentResult> ApplyAsync(TableIdentifier t, params DataOwnerSpec[] owners) =>
        Writer.ApplyTableOwnerAssignmentsAsync(Request(t, owners: owners));

    [Fact]
    public async Task ApplyOwners_CreatesOwnerAndAssignment()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        var result = await ApplyAsync(table, Owner("S-1-5-21-9001", "Ada Owner", "ada@corp.example", "CORP\\ada"));

        result.Success.ShouldBeTrue();
        result.Changes.Select(c => c.Kind).ShouldBe([DataOwnerChangeKind.OwnerCreated, DataOwnerChangeKind.OwnerAssigned]);
        var owners = await Repo.GetDataOwnersForTableAsync(table);
        owners.Single().DisplayName.ShouldBe("Ada Owner");
        Scalar("SELECT id FROM TABLE_OWNERS WHERE table_id = @t", ("@t", TableId(table)))!.ToString()!.ShouldStartWith("dbt:");
        (await Repo.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-9001"))).ShouldBeTrue();
    }

    [Fact]
    public async Task ApplyOwners_SecondRun_IsIdempotent()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        (await ApplyAsync(table, Owner("S-1-5-21-9002"))).Success.ShouldBeTrue();
        var second = await ApplyAsync(table, Owner("S-1-5-21-9002"));

        second.Success.ShouldBeTrue();
        second.Changes.ShouldBeEmpty();
        OwnerRows(table).ShouldBe(1);
    }

    [Fact]
    public async Task ApplyOwners_ExistingOwnerDifferentEmail_RejectsAndWritesNothing()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        SeedOwner(Guid.NewGuid().ToString(), "S-1-5-21-9003", "CORP\\carl", "Carl", "carl@corp.example");
        var before = TotalOwnerRows();

        var result = await ApplyAsync(table, Owner("S-1-5-21-9003", "Carl", "attacker@corp.example"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("dbt_owner_conflict");
        OwnerRows(table).ShouldBe(0);
        TotalOwnerRows().ShouldBe(before);
        Scalar("SELECT email FROM DATA_OWNERS WHERE ad_sid = 'S-1-5-21-9003'").ShouldBe("carl@corp.example");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("corp\\DORA", null, null)]
    [InlineData("S-1-5-21-9099", "DORA@corp.example", null)]
    [InlineData("S-1-5-21-9099", null, "dora@CORP.example")]
    [InlineData("dora@corp.EXAMPLE", null, null)]
    public async Task Apply_IdentifierCollision_CaseVariantAcrossColumns_Rejects(string sid, string? email, string? account)
    {
        // I393B-T-07 / SC-05: a new owner may not share any identifier (sid, account, e-mail; any case) with another row; approver matching reads all three columns.
        if (!Available) { return; }

        var table = await SeedTableAsync();
        SeedOwner(Guid.NewGuid().ToString(), "S-1-5-21-9004", "CORP\\dora", "Dora", "dora@corp.example");
        var before = TotalOwnerRows();

        var result = await ApplyAsync(table, Owner(sid, "Impostor", email, account));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("dbt_owner_identifier_collision");
        OwnerRows(table).ShouldBe(0);
        TotalOwnerRows().ShouldBe(before);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Apply_IdentifierCollision_UnicodeNormalizationVariant_Rejects()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        SeedOwner(Guid.NewGuid().ToString(), "S-1-5-21-9005", "CORP\\René", "Rene", "rene@corp.example");

        var result = await ApplyAsync(table, Owner("corp\\RENÉ", "Impostor"));

        result.ErrorCode.ShouldBe("dbt_owner_identifier_collision");
    }

    [Fact]
    public async Task ApplyOwners_InactiveOwner_Rejects()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        SeedOwner(Guid.NewGuid().ToString(), "S-1-5-21-9006", "CORP\\eve", "Eve", "eve@corp.example", active: 0);

        var result = await ApplyAsync(table, Owner("S-1-5-21-9006", null));

        result.ErrorCode.ShouldBe("dbt_owner_inactive");
        OwnerRows(table).ShouldBe(0);
    }

    [Fact]
    public async Task ApplyOwners_MissingTable_Rejects_AndRollsBackEarlierTables()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        var missing = new TableIdentifier("Sales", "Crm", "Nope_" + Guid.NewGuid().ToString("N")[..6]);
        var before = TotalOwnerRows();
        var request = new DataOwnerAssignmentRequest(
            [new TableOwnerAssignmentSpec(table, [Owner("S-1-5-21-9007")]), new TableOwnerAssignmentSpec(missing, [Owner("S-1-5-21-9008")])],
            false, false);

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(request);

        result.ErrorCode.ShouldBe("dbt_owner_table_not_found");
        result.ErrorTable.ShouldBe(missing.ToString());
        TotalOwnerRows().ShouldBe(before);
        OwnerRows(table).ShouldBe(0);
    }

    [Fact]
    public async Task ApplyOwners_ExistingManualRow_NoDuplicate()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        var ownerId = Guid.NewGuid().ToString();
        SeedOwner(ownerId, "S-1-5-21-9009", "CORP\\fay", "Fay", "fay@corp.example");
        SeedManualAssignment(table, ownerId);

        var result = await ApplyAsync(table, Owner("S-1-5-21-9009", null));

        result.Success.ShouldBeTrue();
        result.Changes.ShouldBeEmpty();
        OwnerRows(table).ShouldBe(1);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Replace_DeletesOnlyDbtPrefixedRows()
    {
        // I393B-T-09 (persistence part): manual rows survive replace; stale dbt rows go; the new owner is added.
        if (!Available) { return; }

        var table = await SeedTableAsync();
        var manualOwner = Guid.NewGuid().ToString();
        SeedOwner(manualOwner, "S-1-5-21-9010", "CORP\\gil", "Gil", "gil@corp.example");
        SeedManualAssignment(table, manualOwner);
        (await ApplyAsync(table, Owner("S-1-5-21-9011", "Hal"))).Success.ShouldBeTrue();
        OwnerRows(table).ShouldBe(2);

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(Request(table, replace: true, owners: Owner("S-1-5-21-9012", "Ivy")));

        result.Success.ShouldBeTrue();
        result.Changes.Select(c => c.Kind).ShouldBe([DataOwnerChangeKind.OwnerCreated, DataOwnerChangeKind.OwnerAssigned, DataOwnerChangeKind.OwnerRemoved]);
        result.Changes.Single(c => c.Kind == DataOwnerChangeKind.OwnerRemoved).RemovedAll.ShouldBeFalse();
        var owners = (await Repo.GetDataOwnersForTableAsync(table)).Select(o => o.DisplayName).OrderBy(n => n).ToList();
        owners.ShouldBe(["Gil", "Ivy"]);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Replace_EmptyOwnerList_RemovesDbtRows_FlagsRemovedAll()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        (await ApplyAsync(table, Owner("S-1-5-21-9013", "Jon"))).Success.ShouldBeTrue();

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(Request(table, replace: true));

        result.Changes.Single().Kind.ShouldBe(DataOwnerChangeKind.OwnerRemoved);
        result.Changes.Single().RemovedAll.ShouldBeTrue();
        OwnerRows(table).ShouldBe(0);
        // the DATA_OWNERS row itself is never deleted
        Count("SELECT COUNT(*) FROM DATA_OWNERS WHERE ad_sid = 'S-1-5-21-9013'").ShouldBe(1);
    }

    [Fact]
    public async Task ApplyOwners_DryRun_RollsBack_AndReportsChanges()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        var before = TotalOwnerRows();

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(Request(table, dryRun: true, owners: Owner("S-1-5-21-9014", "Kim")));

        result.Success.ShouldBeTrue();
        result.Changes.Count.ShouldBe(2);
        TotalOwnerRows().ShouldBe(before);
        OwnerRows(table).ShouldBe(0);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ApplyOwners_NewOwnerCap_Rejects_NothingWritten()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        var before = TotalOwnerRows();

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(
            Request(table, maxNew: 1, owners: [Owner("S-1-5-21-9015", "A"), Owner("S-1-5-21-9016", "B")]));

        result.ErrorCode.ShouldBe("dbt_owner_cap_exceeded");
        TotalOwnerRows().ShouldBe(before);
    }

    [Fact]
    public async Task ApplyOwners_CreationWithoutDisplayName_Rejects()
    {
        if (!Available) { return; }

        var table = await SeedTableAsync();
        (await ApplyAsync(table, Owner("S-1-5-21-9017", null))).ErrorCode.ShouldBe("dbt_owner_display_name_required");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ApplyOwners_BareStringLookup_ResolvesExactlyOneActiveRow_NeverCreates()
    {
        // I393B-SC-09 / T-11 (persistence part)
        if (!Available) { return; }

        var table = await SeedTableAsync();
        var before = TotalOwnerRows();
        (await ApplyAsync(table, new DataOwnerSpec("nobody@corp.example", null, null, null, "PRIMARY", LookupOnly: true))).ErrorCode.ShouldBe("dbt_owner_lookup_failed");
        TotalOwnerRows().ShouldBe(before);

        SeedOwner(Guid.NewGuid().ToString(), "S-1-5-21-9018", "CORP\\lee", "Lee", "lee@corp.example");
        var found = await ApplyAsync(table, new DataOwnerSpec("LEE@corp.example", null, null, null, "DELEGATE", LookupOnly: true));
        found.Success.ShouldBeTrue();
        found.Changes.Single().Role.ShouldBe("DELEGATE");
        Scalar("SELECT owner_role FROM TABLE_OWNERS WHERE table_id = @t", ("@t", TableId(table))).ShouldBe("DELEGATE");

        // a second row sharing the e-mail makes the lookup ambiguous (legacy data)
        SeedOwner(Guid.NewGuid().ToString(), "S-1-5-21-9019", "CORP\\lee2", "Lee 2", "lee@corp.example");
        var other = await SeedTableAsync();
        (await ApplyAsync(other, new DataOwnerSpec("lee@corp.example", null, null, null, "PRIMARY", LookupOnly: true))).ErrorCode.ShouldBe("dbt_owner_lookup_failed");
    }

    [Fact]
    public async Task ApplyOwners_SameNewOwnerOnTwoTables_CreatesOneRow()
    {
        if (!Available) { return; }

        var a = await SeedTableAsync();
        var b = await SeedTableAsync();
        var before = TotalOwnerRows();
        var request = new DataOwnerAssignmentRequest(
            [new TableOwnerAssignmentSpec(a, [Owner("S-1-5-21-9020", "Max")]), new TableOwnerAssignmentSpec(b, [Owner("S-1-5-21-9020", "Max")])],
            false, false);

        var result = await Writer.ApplyTableOwnerAssignmentsAsync(request);

        result.Success.ShouldBeTrue();
        TotalOwnerRows().ShouldBe(before + 1);
        result.Changes.Count(c => c.Kind == DataOwnerChangeKind.OwnerCreated).ShouldBe(1);
        OwnerRows(a).ShouldBe(1);
        OwnerRows(b).ShouldBe(1);
    }
}
