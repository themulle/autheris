using System.Data.Common;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Governance;

/// <summary>
/// PLAN-GOV-158-INC1 WP-158-1: <c>CONSENTS.description</c> persistence contract. Runs on SQLite (unit) and on PostgreSQL and SQL Server
/// (integration, linked). One database serves one test.
/// </summary>
public abstract class ConsentDescriptionRepositoryContractBase : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("tenant-desc");
    private static readonly Sid Alice = new("S-1-5-21-ALICE-DESC");
    private static readonly Sid Admin = new("S-1-5-21-ADMIN-DESC");

    protected IGovernanceRepository Repo { get; private set; } = null!;

    private IConsentDescriptionRepository Desc => (IConsentDescriptionRepository)Repo;

    private async Task<string?> DescriptionOfAsync(Guid id)
    {
        var consent = (await Repo.GetConsentByIdAsync(id))!;
        await Desc.LoadDescriptionsAsync([consent]);
        return consent.Description;
    }
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

    private static TableIdentifier NewTable() => new("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..8]);

    private async Task<Guid> UpsertTableAsync(TableIdentifier id)
    {
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName,
            Sensitivity = "LOW", RequiresFourEyes = false, IsActive = true
        };
        return (await Repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id, Table = table, Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        })).Table.Id;
    }

    private static Consent NewConsent(Guid tableId, TableIdentifier table, GranteeType type, string? description, string? roleName = null, DateTimeOffset? validTo = null) => new()
    {
        TableId = tableId, TableIdentifier = table, TenantId = Tenant, GranteeType = type,
        GranteeSid = type == GranteeType.Role ? (Sid?)null : Alice, RoleName = roleName,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = validTo ?? DateTimeOffset.UtcNow.AddDays(30),
        CreatedBySid = Admin, Description = description
    };

    protected async Task<(TableIdentifier Table, Guid TableId)> SeedTableAsync()
    {
        var table = NewTable();
        return (table, await UpsertTableAsync(table));
    }

    [Fact]
    public async Task ConsentDescription_RoundTrip_AllReadMethods()
    {
        if (!Available) return;
        var (table, tableId) = await SeedTableAsync();
        var user = await Repo.CreateConsentAsync(NewConsent(tableId, table, GranteeType.User, "Sales analysts read orders", validTo: DateTimeOffset.UtcNow.AddHours(2)));
        var role = await Repo.CreateConsentAsync(NewConsent(tableId, table, GranteeType.Role, "Role text", roleName: "Analyst"));
        var none = await Repo.CreateConsentAsync(NewConsent(tableId, table, GranteeType.User, null));
        user.Description.ShouldBe("Sales analysts read orders");

        // The access-decision hot path never loads the description: every read method leaves it unset until the explicit opt-in load.
        (await Repo.GetConsentByIdAsync(user.Id))!.Description.ShouldBeNull();
        (await DescriptionOfAsync(user.Id)).ShouldBe("Sales analysts read orders");
        (await DescriptionOfAsync(none.Id)).ShouldBeNull();

        var now = DateTimeOffset.UtcNow;
        var forSubjects = await Repo.GetActiveConsentsForSubjectsAsync([Alice], table, now, Tenant);
        forSubjects.ShouldAllBe(c => c.Description == null);
        await Desc.LoadDescriptionsAsync(forSubjects);
        forSubjects.Single(c => c.Id == user.Id).Description.ShouldBe("Sales analysts read orders");
        forSubjects.Single(c => c.Id == none.Id).Description.ShouldBeNull();

        var all = await Repo.GetAllActiveConsentsForSubjectsAsync([Alice], ["Analyst"], now, Tenant);
        all.ShouldAllBe(c => c.Description == null);
        await Desc.LoadDescriptionsAsync(all);
        all.Single(c => c.Id == user.Id).Description.ShouldBe("Sales analysts read orders");

        var roles = await Repo.GetActiveRoleConsentsAsync(table, "Analyst", Tenant, now);
        await Desc.LoadDescriptionsAsync(roles);
        roles.Single(c => c.Id == role.Id).Description.ShouldBe("Role text");

        var page = await Repo.GetConsentPageAsync(new ConsentPageQuery(Tenant, table));
        page.Items.ShouldAllBe(c => c.Description == null);
        await Desc.LoadDescriptionsAsync(page.Items);
        page.Items.Single(c => c.Id == user.Id).Description.ShouldBe("Sales analysts read orders");
        page.Items.Single(c => c.Id == role.Id).Description.ShouldBe("Role text");

        var expiring = await Repo.GetExpiringConsentsAsync(now.AddHours(3));
        expiring.ShouldAllBe(c => c.Description == null);
        await Desc.LoadDescriptionsAsync(expiring);
        expiring.Single(c => c.Id == user.Id).Description.ShouldBe("Sales analysts read orders");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task DbCheck_DescriptionOver2000_Rejected()
    {
        // T-GOV158I1-16 (SEC-GOV158I1-11): a direct write cannot store unbounded text.
        if (!Available) return;
        var (table, tableId) = await SeedTableAsync();
        var consent = await Repo.CreateConsentAsync(NewConsent(tableId, table, GranteeType.User, "ok"));

        await using var conn = OpenRaw();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE CONSENTS SET description = @d WHERE id = @id";
        AddParameter(cmd, "@d", new string('x', 2001));
        AddParameter(cmd, "@id", consent.Id.ToString());

        await Should.ThrowAsync<DbException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Replace_DescriptionUpdate_ChangesOnlyTheDescription()
    {
        if (!Available) return;
        var (table, tableId) = await SeedTableAsync();
        var consent = await Repo.CreateConsentAsync(NewConsent(tableId, table, GranteeType.User, "before"));

        await Desc.ReplaceConsentsAsync([], [], Admin, "desc", null, [new ConsentDescriptionUpdate(consent.Id, Tenant, "after")], CancellationToken.None);

        var loaded = (await Repo.GetConsentByIdAsync(consent.Id))!;
        (await DescriptionOfAsync(consent.Id)).ShouldBe("after");
        loaded.IsRevoked.ShouldBeFalse();
        loaded.ValidTo.ShouldBe(consent.ValidTo);
        loaded.Effect.ShouldBe(consent.Effect);

        await Desc.ReplaceConsentsAsync([], [], Admin, "desc", null, [new ConsentDescriptionUpdate(consent.Id, Tenant, null)], CancellationToken.None);
        (await DescriptionOfAsync(consent.Id)).ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Apply_DescriptionOnly_ConcurrentRevoke_NotResurrected()
    {
        // T-GOV158I1-09 (SEC-GOV158I1-05/07): the in-place update never touches a revoked consent and never un-revokes it.
        if (!Available) return;
        var (table, tableId) = await SeedTableAsync();
        var consent = await Repo.CreateConsentAsync(NewConsent(tableId, table, GranteeType.User, "before"));
        await Repo.RevokeConsentAsync(consent.Id, Alice, "revoked in between");

        await Desc.ReplaceConsentsAsync([], [], Admin, "desc", null, [new ConsentDescriptionUpdate(consent.Id, Tenant, "after")], CancellationToken.None);

        var loaded = (await Repo.GetConsentByIdAsync(consent.Id))!;
        loaded.IsRevoked.ShouldBeTrue();
        (await DescriptionOfAsync(consent.Id)).ShouldBe("before");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Replace_DescriptionUpdate_OtherTenant_ChangesNothing()
    {
        if (!Available) return;
        var (table, tableId) = await SeedTableAsync();
        var consent = await Repo.CreateConsentAsync(NewConsent(tableId, table, GranteeType.User, "before"));

        await Desc.ReplaceConsentsAsync([], [], Admin, "desc", null, [new ConsentDescriptionUpdate(consent.Id, new TenantId("tenant-other"), "after")], CancellationToken.None);

        (await DescriptionOfAsync(consent.Id)).ShouldBe("before");
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
