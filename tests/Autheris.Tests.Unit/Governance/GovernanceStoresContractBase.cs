using Autheris.Domain.Common;
using Autheris.Application.Interfaces;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Governance;

/// <summary>
/// PERS-16 / PERS-07 / PERS-09 / GOV-02 contract of the identity (SCIM), functional-right and recertification stores. One governance
/// repository implements all three interfaces; run on SQLite (unit) and on PostgreSQL and SQL Server (integration). Every test uses
/// its own ids, so tests share one database.
/// </summary>
public abstract class GovernanceStoresContractBase : IAsyncLifetime
{
    protected static readonly TenantId Tenant = new("tenant-a");
    private const string Realm = "realm-1";

    protected IIdentityStoreRepository Identity { get; private set; } = null!;
    protected IFunctionalRightRepository Rights { get; private set; } = null!;
    protected IRecertificationRepository Recert { get; private set; } = null!;
    protected ITableMetadataRepository Catalog { get; private set; } = null!;
    protected bool Available { get; private set; }

    /// <summary>The repository (implements all three store interfaces) or null when the engine is not available.</summary>
    protected abstract Task<object?> CreateRepositoryAsync();

    protected abstract Task DisposeRepositoryAsync();

    public async Task InitializeAsync()
    {
        var repo = await CreateRepositoryAsync();
        Available = repo != null;
        if (repo != null)
        {
            Identity = (IIdentityStoreRepository)repo;
            Rights = (IFunctionalRightRepository)repo;
            Recert = (IRecertificationRepository)repo;
            Catalog = (ITableMetadataRepository)repo;
        }
    }

    public Task DisposeAsync() => DisposeRepositoryAsync();

    private static string Id(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static DateTimeOffset Now() => new(DateTimeOffset.UtcNow.UtcDateTime.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    // ------------------------------------------------------------------ identity: round trips (PERS-16)

    [Fact]
    public async Task User_RoundTrip_AllColumns_IncludingInactiveAndNullTimestamps()
    {
        if (!Available) return;
        var key = Id("u");
        var created = Now().AddDays(-2);
        var deactivated = Now().AddHours(-1);

        await Identity.UpsertUserAsync(new ScimUser(key, "ext-" + key, "user@example.com", "Display", "user@example.com", false, deactivated, created, created), Tenant, Realm);

        var read = await Identity.GetUserAsync(Tenant, Realm, key);
        read.ShouldNotBeNull();
        read!.Active.ShouldBeFalse();
        read.DeactivatedAt.ShouldBe(deactivated);
        read.CreatedAt.ShouldBe(created);

        var key2 = Id("u");
        await Identity.UpsertUserAsync(new ScimUser(key2, "ext-" + key2, "second@example.com", null, null, true, null, created, created), Tenant, Realm);
        var active = await Identity.GetUserAsync(Tenant, Realm, key2);
        active!.Active.ShouldBeTrue();
        active.DeactivatedAt.ShouldBeNull();
        active.Email.ShouldBeNull();
    }

    [Fact]
    public async Task User_Deactivate_SetsInactiveAndTimestamp()
    {
        if (!Available) return;
        var key = Id("u");
        await Identity.UpsertUserAsync(new ScimUser(key, "ext-" + key, "d@example.com", null, null, true, null, Now(), Now()), Tenant, Realm);

        await Identity.DeactivateUserAsync(Tenant, Realm, key);

        var read = await Identity.GetUserAsync(Tenant, Realm, key);
        read!.Active.ShouldBeFalse();
        read.DeactivatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Lock_RoundTrip_LockedAtPreserved()
    {
        if (!Available) return;
        var key = Id("u");
        var lockedAt = Now().AddMinutes(-5);

        await Identity.AddLockAsync(new ScimLock(Tenant, Realm, key, "reason", "SCIM_DELETE", lockedAt));

        (await Identity.IsSubjectLockedAsync(Tenant, Realm, key)).ShouldBeTrue();
        (await Identity.GetLocksAsync(Tenant, Realm)).ShouldContain(l => l.UserKey == key && l.LockedAt == lockedAt);
    }


    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T10_CountLocksSince_ScopedDistinctWindowedAndBounded()
    {
        if (!Available) return;
        var realm = Id("realm");
        var other = Id("realm");
        var otherTenant = new TenantId(Id("tenant"));
        var now = Now();
        string[] scim = ["SCIM_PUT", "SCIM_PATCH", "SCIM_DELETE"];

        await Identity.AddLockAsync(new ScimLock(Tenant, realm, "a", "r", "SCIM_DELETE", now.AddMinutes(-30)));
        await Identity.AddLockAsync(new ScimLock(Tenant, realm, "b", "r", "SCIM_PUT", now.AddMinutes(-20)));
        await Identity.AddLockAsync(new ScimLock(Tenant, realm, "c", "r", "SCIM_PATCH", now.AddMinutes(-10)));
        await Identity.AddLockAsync(new ScimLock(Tenant, realm, "old", "r", "SCIM_DELETE", now.AddMinutes(-90)));
        await Identity.AddLockAsync(new ScimLock(Tenant, realm, "admin", "r", "admin-sid", now.AddMinutes(-5)));
        await Identity.AddLockAsync(new ScimLock(Tenant, other, "x", "r", "SCIM_DELETE", now.AddMinutes(-5)));
        await Identity.AddLockAsync(new ScimLock(otherTenant, realm, "y", "r", "SCIM_DELETE", now.AddMinutes(-5)));
        // a repeat on an already-locked user is one row, still counted once
        await Identity.AddLockAsync(new ScimLock(Tenant, realm, "a", "r", "SCIM_DELETE", now.AddMinutes(-29)));

        var window = await Identity.CountLocksSinceAsync(Tenant, realm, scim, now.AddMinutes(-60), 100);
        window.Count.ShouldBe(3);
        window.Oldest.ShouldBe(now.AddMinutes(-29));

        (await Identity.CountLocksSinceAsync(Tenant, realm, scim, now.AddMinutes(-60), 2)).Count.ShouldBe(2);
        var empty = await Identity.CountLocksSinceAsync(Tenant, Id("none"), scim, now.AddMinutes(-60), 5);
        (empty.Count, empty.Oldest).ShouldBe((0, null));
    }

    // ------------------------------------------------------------------ identity: deletion (PERS-07)

    private async Task<(string User, string Group)> UserInGroupAsync()
    {
        var user = Id("u");
        var group = Id("g");
        await Identity.UpsertUserAsync(new ScimUser(user, "ext-" + user, user + "@example.com", null, null, true, null, Now(), Now()), Tenant, Realm);
        await Identity.UpsertGroupAsync(new ScimGroup(group, "ext-" + group, "Group", null, Now(), Now()), Tenant, Realm);
        await Identity.AddGroupMemberAsync(Tenant, Realm, group, user);
        return (user, group);
    }

    [Fact]
    public async Task DeleteGroup_RemovesMemberships_AndReturnsFormerMembers()
    {
        if (!Available) return;
        var (user, group) = await UserInGroupAsync();
        (await Identity.GetGroupMembershipsForUserAsync(Tenant, Realm, user)).ShouldContain(group);

        var former = await Identity.DeleteGroupAsync(Tenant, Realm, group);

        former.ShouldContain(user);
        (await Identity.GetGroupMembershipsForUserAsync(Tenant, Realm, user)).ShouldNotContain(group);
        (await Identity.GetGroupMembersAsync(Tenant, Realm, group)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteGroup_RecreatedSameId_HasNoStaleMembers()
    {
        if (!Available) return;
        var (user, group) = await UserInGroupAsync();
        await Identity.DeleteGroupAsync(Tenant, Realm, group);

        await Identity.UpsertGroupAsync(new ScimGroup(group, "ext-" + group, "Recreated", null, Now(), Now()), Tenant, Realm);

        (await Identity.GetGroupMembershipsForUserAsync(Tenant, Realm, user)).ShouldNotContain(group);
    }

    [Fact]
    public async Task DeleteUser_RemovesMemberships_KeepsLock()
    {
        if (!Available) return;
        var (user, group) = await UserInGroupAsync();
        await Identity.AddLockAsync(new ScimLock(Tenant, Realm, user, "deprovisioned", "SCIM_DELETE", Now()));

        await Identity.DeleteUserAsync(Tenant, Realm, user);

        (await Identity.GetUserAsync(Tenant, Realm, user)).ShouldBeNull();
        (await Identity.GetGroupMembersAsync(Tenant, Realm, group)).ShouldNotContain(user);
        (await Identity.IsSubjectLockedAsync(Tenant, Realm, user)).ShouldBeTrue();
    }

    [Fact]
    public async Task Memberships_OrphanRow_NotReturned()
    {
        if (!Available) return;
        var user = Id("u");
        var missingGroup = Id("g");

        // SCIM may add members before the group exists; such an orphan never resolves to a group.
        await Identity.AddGroupMemberAsync(Tenant, Realm, missingGroup, user);

        (await Identity.GetGroupMembershipsForUserAsync(Tenant, Realm, user)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ functional rights (PERS-16, PERS-09)

    private static FunctionalRightMapping Mapping(string groupOrRole, FunctionalRight right, string? approvedBy, string createdBy = "creator", string realm = Realm) =>
        new(Tenant, realm, groupOrRole, right, approvedBy, approvedBy is null ? null : Now(), "Standard", createdBy, Now());

    [Fact]
    public async Task Mapping_RoundTrip_PreservesTimestamps()
    {
        if (!Available) return;
        var group = Id("grp");
        var created = Mapping(group, FunctionalRight.DataExport, "approver");

        await Rights.CreateMappingAsync(created);

        var read = await Rights.GetMappingAsync(Tenant, Realm, group, FunctionalRight.DataExport);
        read.ShouldNotBeNull();
        read!.ApprovedBy.ShouldBe("approver");
        read.ApprovedAt.ShouldBe(created.ApprovedAt);
        read.CreatedAt.ShouldBe(created.CreatedAt);
    }

    [Fact]
    public async Task ApprovedMappings_SelfApprovedPrivileged_Excluded()
    {
        if (!Available) return;
        var group = Id("grp");
        await Rights.CreateMappingAsync(Mapping(group, FunctionalRight.DataUnmaskSensitive, "Same-Person", "same-person"));
        await Rights.CreateMappingAsync(Mapping(group, FunctionalRight.DataExport, "same-person", "same-person"));

        var approved = await Rights.GetApprovedMappingsForGroupsAndRolesAsync(Tenant, Realm, [group]);

        approved.ShouldNotContain(m => m.FunctionalRight == FunctionalRight.DataUnmaskSensitive);
        approved.ShouldContain(m => m.FunctionalRight == FunctionalRight.DataExport);
    }

    [Fact]
    public async Task ApprovedMappings_FourEyesPrivileged_Included()
    {
        if (!Available) return;
        var group = Id("grp");
        await Rights.CreateMappingAsync(Mapping(group, FunctionalRight.DataUnmaskSensitive, "approver", "creator"));

        var approved = await Rights.GetApprovedMappingsForGroupsAndRolesAsync(Tenant, Realm, [group]);

        approved.ShouldContain(m => m.FunctionalRight == FunctionalRight.DataUnmaskSensitive);
    }

    [Fact]
    public async Task ApprovedMappings_GroupCaseDiffers_Matches()
    {
        if (!Available) return;
        var group = Id("Grp");
        await Rights.CreateMappingAsync(Mapping(group, FunctionalRight.DataExport, "approver"));

        var approved = await Rights.GetApprovedMappingsForGroupsAndRolesAsync(Tenant, Realm, [group.ToLowerInvariant()]);

        approved.ShouldContain(m => m.GroupOrRole == group);
    }

    // ------------------------------------------------------------------ recertification (PERS-16, GOV-02)

    private async Task<(string Campaign, string Item)> CampaignWithItemAsync(DateTimeOffset? deadline = null)
    {
        var campaign = Id("c");
        var item = Id("i");
        await Recert.CreateCampaignAsync(new RecertificationCampaign(
            Tenant.Value, campaign, "Campaign", RecertClass.Standard, RecertCampaignStatus.Active, deadline ?? Now().AddDays(14), Now()));
        await Recert.CreateItemAsync(new RecertificationItem(Tenant.Value, campaign, item, "FunctionalRightMapping", "r:g:Data.Read", "alice", "bob"));
        return (campaign, item);
    }

    [Fact]
    public async Task Recert_CampaignAndItem_RoundTrip_NullDecisionTimestamp()
    {
        if (!Available) return;
        var deadline = Now().AddDays(7);
        var (campaign, item) = await CampaignWithItemAsync(deadline);

        var readCampaign = await Recert.GetCampaignByIdAsync(Tenant, campaign);
        readCampaign.ShouldNotBeNull();
        readCampaign!.Deadline.ShouldBe(deadline);

        var readItem = await Recert.GetItemByIdAsync(Tenant, campaign, item);
        readItem.ShouldNotBeNull();
        readItem!.Decision.ShouldBe(RecertDecision.Pending);
        readItem.DecisionAt.ShouldBeNull();
    }

    [Fact]
    public async Task Recert_TryUpdateItemDecision_IsCompareAndSet_AndStoresDecisionTimestamp()
    {
        if (!Available) return;
        var (campaign, item) = await CampaignWithItemAsync();

        (await Recert.TryUpdateItemDecisionAsync(Tenant, campaign, item, RecertDecision.Certified, RecertDecision.Revoked, "bob")).ShouldBeFalse();
        (await Recert.TryUpdateItemDecisionAsync(Tenant, campaign, item, RecertDecision.Pending, RecertDecision.Certified, "bob")).ShouldBeTrue();
        (await Recert.TryUpdateItemDecisionAsync(Tenant, campaign, item, RecertDecision.Pending, RecertDecision.Revoked, "carol")).ShouldBeFalse();

        var read = await Recert.GetItemByIdAsync(Tenant, campaign, item);
        read!.Decision.ShouldBe(RecertDecision.Certified);
        read.DecisionBy.ShouldBe("bob");
        read.DecisionAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Recert_PendingPastDeadline_ReturnsOnlyExpiredPendingItems()
    {
        if (!Available) return;
        var (expiredCampaign, expiredItem) = await CampaignWithItemAsync(Now().AddDays(-1));
        var (_, openItem) = await CampaignWithItemAsync(Now().AddDays(10));

        var pending = await Recert.GetPendingItemsPastDeadlineAsync(Now());

        pending.ShouldContain(i => i.CampaignId == expiredCampaign && i.ItemId == expiredItem);
        pending.ShouldNotContain(i => i.ItemId == openItem);
    }

    // ------------------------------------------------------------------ catalog masking rules (PERS-14)

    private static TableMetadata MaskedTable(string tableName, Guid ruleId, params string[] maskedColumns)
    {
        var id = Guid.NewGuid();
        var columns = maskedColumns.Select(c => new TableColumn { TableId = id, ColumnName = c, DataType = "TEXT", IsSensitive = true }).ToList();
        return new TableMetadata
        {
            Identifier = new TableIdentifier("MaskDomain", "dbo", tableName),
            Table = new Table { Id = id, SourceName = "MaskDomain", SchemaName = "dbo", TableName = tableName, Sensitivity = "LOW", IsActive = true },
            Columns = columns,
            ColumnMaskingRules = maskedColumns.ToDictionary(c => c, c => new MaskingRule { Id = ruleId, RuleType = "REDACT" })
        };
    }

    [Fact]
    public async Task Upsert_ReusedRuleIdAcrossColumns_BothColumnsMasked()
    {
        if (!Available) return;
        var name = Id("mask");

        await Catalog.UpsertTableMetadataAsync(MaskedTable(name, Guid.NewGuid(), "email", "phone"));

        var read = await Catalog.GetTableMetadataAsync(new TableIdentifier("MaskDomain", "dbo", name));
        read.ShouldNotBeNull();
        read!.ColumnMaskingRules.Keys.ShouldBe(["email", "phone"], ignoreOrder: true);
    }

    [Fact]
    public async Task Upsert_RuleIdOfOtherTable_ColumnMasked()
    {
        if (!Available) return;
        var shared = Guid.NewGuid();
        var first = Id("maskA");
        var second = Id("maskB");

        await Catalog.UpsertTableMetadataAsync(MaskedTable(first, shared, "email"));
        await Catalog.UpsertTableMetadataAsync(MaskedTable(second, shared, "email"));

        (await Catalog.GetTableMetadataAsync(new TableIdentifier("MaskDomain", "dbo", first)))!.ColumnMaskingRules.ShouldContainKey("email");
        (await Catalog.GetTableMetadataAsync(new TableIdentifier("MaskDomain", "dbo", second)))!.ColumnMaskingRules.ShouldContainKey("email");
    }
}
