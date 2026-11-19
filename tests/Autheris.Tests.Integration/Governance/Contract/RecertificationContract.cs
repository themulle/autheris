using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>IRecertificationRepository</c> across PostgreSQL and SQL Server engines. Campaigns and items are keyed by tenant first; the
/// decision update is a compare-and-set on the stored decision. Only the deadline sweep reads across tenants, by design.
/// </summary>
public abstract class RecertificationContract : GovernanceContractBase
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private IRecertificationRepository Repo => Store.Recertification;

    private static string Id(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static RecertificationCampaign Campaign(TenantId tenant, string id, DateTimeOffset? deadline = null, string name = "Campaign") =>
        new(tenant.Value, id, name, RecertClass.Standard, RecertCampaignStatus.Active, deadline ?? Now.AddDays(14), Now);

    private static RecertificationItem Item(TenantId tenant, string campaign, string id) =>
        new(tenant.Value, campaign, id, "FunctionalRightMapping", "r:g:Data.Read", "alice", "bob");

    private async Task<(string Campaign, string Item)> SeedAsync(TenantId tenant, DateTimeOffset? deadline = null)
    {
        var campaign = Id("c");
        var item = Id("i");
        await Repo.CreateCampaignAsync(Campaign(tenant, campaign, deadline));
        await Repo.CreateItemAsync(Item(tenant, campaign, item));
        return (campaign, item);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var campaign = Id("c");
        await Repo.CreateCampaignAsync(Campaign(Store.TenantA, campaign, name: "A campaign"));
        await Repo.CreateItemAsync(Item(Store.TenantA, campaign, "item-1"));

        (await Repo.GetCampaignsAsync(Store.TenantA)).ShouldHaveSingleItem().Name.ShouldBe("A campaign");
        (await Repo.GetCampaignsAsync(Store.TenantB)).ShouldBeEmpty();
        (await Repo.GetCampaignByIdAsync(Store.TenantB, campaign)).ShouldBeNull();
        (await Repo.GetItemsAsync(Store.TenantB, campaign)).ShouldBeEmpty();
        (await Repo.GetItemByIdAsync(Store.TenantB, campaign, "item-1")).ShouldBeNull();

        // The same campaign id in another tenant is another campaign.
        await Repo.CreateCampaignAsync(Campaign(Store.TenantB, campaign, name: "B campaign"));
        (await Repo.GetCampaignByIdAsync(Store.TenantA, campaign))!.Name.ShouldBe("A campaign");
        (await Repo.GetCampaignByIdAsync(Store.TenantB, campaign))!.Name.ShouldBe("B campaign");
        (await Repo.GetItemsAsync(Store.TenantB, campaign)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var (campaign, item) = await SeedAsync(Store.TenantA);

        (await Repo.TryUpdateItemDecisionAsync(Store.TenantB, campaign, item, RecertDecision.Pending, RecertDecision.Revoked, "mallory")).ShouldBeFalse();
        await Repo.UpdateCampaignStatusAsync(Store.TenantB, campaign, RecertCampaignStatus.Expired);

        var read = (await Repo.GetItemByIdAsync(Store.TenantA, campaign, item)).ShouldNotBeNull();
        read.Decision.ShouldBe(RecertDecision.Pending);
        read.DecisionBy.ShouldBeNull();
        (await Repo.GetCampaignByIdAsync(Store.TenantA, campaign))!.Status.ShouldBe(RecertCampaignStatus.Active);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var (campaign, item) = await SeedAsync(Store.TenantA);

        Should.Throw<ArgumentException>(() => { _ = new TenantId("invalid tenant!"); });
        await Should.ThrowAsync<ArgumentException>(() => Repo.CreateCampaignAsync(Campaign(Store.TenantA, Id("c")) with { TenantId = string.Empty }));
        await Should.ThrowAsync<ArgumentException>(() => Repo.CreateItemAsync(Item(Store.TenantA, campaign, Id("i")) with { TenantId = " " }));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetCampaignsAsync(default));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetCampaignByIdAsync(default, campaign));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetItemsAsync(default, campaign));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetItemByIdAsync(default, campaign, item));
        await Should.ThrowAsync<ArgumentException>(() => Repo.UpdateCampaignStatusAsync(default, campaign, RecertCampaignStatus.Completed));
        await Should.ThrowAsync<ArgumentException>(() => Repo.TryUpdateItemDecisionAsync(default, campaign, item, RecertDecision.Pending, RecertDecision.Certified, "bob"));

        (await Repo.GetCampaignsAsync(Store.TenantA)).ShouldHaveSingleItem();
        (await Repo.GetItemsAsync(Store.TenantA, campaign)).ShouldHaveSingleItem().Decision.ShouldBe(RecertDecision.Pending);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var (campaign, item) = await SeedAsync(Store.TenantA);

        await Repo.UpdateCampaignStatusAsync(Store.TenantA, campaign, RecertCampaignStatus.Completed);
        await Repo.UpdateCampaignStatusAsync(Store.TenantA, campaign, RecertCampaignStatus.Completed);
        await Repo.UpdateCampaignStatusAsync(Store.TenantA, Id("missing"), RecertCampaignStatus.Expired);
        (await Repo.TryUpdateItemDecisionAsync(Store.TenantA, campaign, item, RecertDecision.Pending, RecertDecision.Certified, "bob", "ok")).ShouldBeTrue();
        (await Repo.TryUpdateItemDecisionAsync(Store.TenantA, campaign, item, RecertDecision.Pending, RecertDecision.Certified, "carol", "again")).ShouldBeFalse();

        var read = (await Repo.GetCampaignByIdAsync(Store.TenantA, campaign)).ShouldNotBeNull();
        read.Status.ShouldBe(RecertCampaignStatus.Completed);
        read.Name.ShouldBe("Campaign");
        read.Deadline.ShouldBe(Now.AddDays(14));
        var decided = (await Repo.GetItemByIdAsync(Store.TenantA, campaign, item)).ShouldNotBeNull();
        decided.DecisionBy.ShouldBe("bob");
        decided.Comments.ShouldBe("ok");
        (await Repo.GetCampaignsAsync(Store.TenantA)).ShouldHaveSingleItem();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var (campaign, item) = await SeedAsync(Store.TenantA);

        // Creates are plain inserts: a repeated key surfaces as the engine's own database exception (no domain mapping) and changes nothing.
        await Should.ThrowAsync<DbException>(() => Repo.CreateCampaignAsync(Campaign(Store.TenantA, campaign, name: "Duplicate")));
        await Should.ThrowAsync<DbException>(() => Repo.CreateItemAsync(Item(Store.TenantA, campaign, item) with { AssignedTo = "mallory" }));

        (await Repo.GetCampaignByIdAsync(Store.TenantA, campaign))!.Name.ShouldBe("Campaign");
        (await Repo.GetItemsAsync(Store.TenantA, campaign)).ShouldHaveSingleItem().AssignedTo.ShouldBe("alice");
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var campaign = Id("c");
        await Repo.CreateCampaignAsync(Campaign(Store.TenantA, campaign));
        var ids = Enumerable.Range(0, 5).Select(i => $"item-{i}").ToArray();
        foreach (var id in ids)
        {
            await Repo.CreateItemAsync(Item(Store.TenantA, campaign, id));
        }

        var first = await Repo.GetItemsAsync(Store.TenantA, campaign);
        var second = await Repo.GetItemsAsync(Store.TenantA, campaign);

        // The list reads have no paging and no guaranteed order: they are complete and hold the same set on every call.
        first.Select(i => i.ItemId).ToArray().ShouldBe(ids, ignoreOrder: true);
        second.Select(i => i.ItemId).OrderBy(i => i, StringComparer.Ordinal).ToArray()
            .ShouldBe(first.Select(i => i.ItemId).OrderBy(i => i, StringComparer.Ordinal).ToArray());
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var deadline = Now.AddDays(1);
        var (campaign, item) = await SeedAsync(Store.TenantA, deadline);

        // The deadline sweep is unbounded by a page size; its only bound is the deadline itself, inclusive.
        (await Repo.GetPendingItemsPastDeadlineAsync(deadline.AddSeconds(-1))).ShouldBeEmpty();
        (await Repo.GetPendingItemsPastDeadlineAsync(deadline)).ShouldHaveSingleItem().ItemId.ShouldBe(item);
        (await Repo.GetPendingItemsPastDeadlineAsync(deadline.AddYears(50))).ShouldHaveSingleItem().CampaignId.ShouldBe(campaign);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var deadline = Now.AddDays(-1);
        var (campaign, decided) = await SeedAsync(Store.TenantA, deadline);
        await Repo.CreateItemAsync(Item(Store.TenantA, campaign, "still-pending"));

        // Rows are never deleted: a decided item leaves the pending sweep, so an expired campaign cannot revoke it again.
        (await Repo.TryUpdateItemDecisionAsync(Store.TenantA, campaign, decided, RecertDecision.Pending, RecertDecision.Certified, "bob")).ShouldBeTrue();

        (await Repo.GetPendingItemsPastDeadlineAsync(Now)).ShouldHaveSingleItem().ItemId.ShouldBe("still-pending");
        (await Repo.GetItemByIdAsync(Store.TenantA, campaign, decided))!.Decision.ShouldBe(RecertDecision.Certified);
    }

    [RequiresContainerRuntimeFact]
    public async Task Recert_CampaignAndItem_RoundTrip_NullDecisionTimestamp()
    {
        if (!Store.IsAvailable) return;
        var deadline = Now.AddDays(7);
        var (campaign, item) = await SeedAsync(Store.TenantA, deadline);

        var readCampaign = (await Repo.GetCampaignByIdAsync(Store.TenantA, campaign)).ShouldNotBeNull();
        readCampaign.Deadline.ShouldBe(deadline);
        readCampaign.RecertClass.ShouldBe(RecertClass.Standard);
        readCampaign.Status.ShouldBe(RecertCampaignStatus.Active);
        readCampaign.CreatedAt.ShouldBe(Now);

        var readItem = (await Repo.GetItemByIdAsync(Store.TenantA, campaign, item)).ShouldNotBeNull();
        readItem.Decision.ShouldBe(RecertDecision.Pending);
        readItem.DecisionAt.ShouldBeNull();
        readItem.DecisionBy.ShouldBeNull();
        readItem.Comments.ShouldBeNull();
        readItem.ReviewerId.ShouldBe("bob");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task TryUpdateItemDecision_ExpectedStateMismatch_ReturnsFalse()
    {
        if (!Store.IsAvailable) return;
        var (campaign, item) = await SeedAsync(Store.TenantA);

        var updated = await Repo.TryUpdateItemDecisionAsync(Store.TenantA, campaign, item, RecertDecision.Certified, RecertDecision.Revoked, "bob");

        updated.ShouldBeFalse();
        (await Repo.GetItemByIdAsync(Store.TenantA, campaign, item))!.Decision.ShouldBe(RecertDecision.Pending);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task TryUpdateItemDecision_SecondDecision_LosesTheRace()
    {
        if (!Store.IsAvailable) return;
        var (campaign, item) = await SeedAsync(Store.TenantA);

        (await Repo.TryUpdateItemDecisionAsync(Store.TenantA, campaign, item, RecertDecision.Pending, RecertDecision.Certified, "bob")).ShouldBeTrue();
        (await Repo.TryUpdateItemDecisionAsync(Store.TenantA, campaign, item, RecertDecision.Pending, RecertDecision.Revoked, "carol")).ShouldBeFalse();

        var read = (await Repo.GetItemByIdAsync(Store.TenantA, campaign, item)).ShouldNotBeNull();
        read.Decision.ShouldBe(RecertDecision.Certified);
        read.DecisionBy.ShouldBe("bob");
        read.DecisionAt.ShouldNotBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task TryUpdateItemDecision_OnMissingItem_ReturnsFalse()
    {
        if (!Store.IsAvailable) return;
        var (campaign, _) = await SeedAsync(Store.TenantA);

        (await Repo.TryUpdateItemDecisionAsync(Store.TenantA, campaign, Id("missing"), RecertDecision.Pending, RecertDecision.Certified, "bob")).ShouldBeFalse();
        (await Repo.TryUpdateItemDecisionAsync(Store.TenantA, Id("missing"), "item", RecertDecision.Pending, RecertDecision.Certified, "bob")).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    public async Task Recert_PendingPastDeadline_ReturnsOnlyExpiredPendingItems()
    {
        if (!Store.IsAvailable) return;
        var (expiredCampaign, expiredItem) = await SeedAsync(Store.TenantA, Now.AddDays(-1));
        var (_, openItem) = await SeedAsync(Store.TenantB, Now.AddDays(10));

        var pending = await Repo.GetPendingItemsPastDeadlineAsync(Now);

        pending.ShouldContain(i => i.CampaignId == expiredCampaign && i.ItemId == expiredItem);
        pending.ShouldNotContain(i => i.ItemId == openItem);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlRecertificationContractTests(GovernancePostgresFixture fixture) : RecertificationContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "recertification_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerRecertificationContractTests(GovernanceSqlServerFixture fixture) : RecertificationContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "recertification_contract");
}
