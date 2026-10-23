namespace Autheris.Tests.Unit.Governance.Recertification;

using System;
using System.Threading.Tasks;
using Autheris.Infrastructure.Cache;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>GOV-02 / PERS-12: the decision update is a compare-and-set on the stored decision (SQLite).</summary>
[Trait("Category", "Security")]
public sealed class RecertificationStoreCompareAndSetTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant-1");
    private readonly SqliteGovernanceRepository _repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());

    public void Dispose() => _repo.Dispose();

    private async Task SeedAsync()
    {
        await _repo.CreateCampaignAsync(new RecertificationCampaign(
            "tenant-1", "camp", "c", RecertClass.Standard, RecertCampaignStatus.Active,
            DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow));
        await _repo.CreateItemAsync(new RecertificationItem(
            "tenant-1", "camp", "item", "FunctionalRightMapping", "r:g:Data.Read", "alice", "bob"));
    }

    [Fact]
    public async Task TryUpdateItemDecision_ExpectedStateMismatch_ReturnsFalse()
    {
        await SeedAsync();

        var updated = await _repo.TryUpdateItemDecisionAsync(
            Tenant, "camp", "item", RecertDecision.Certified, RecertDecision.Revoked, "bob", null);

        updated.ShouldBeFalse();
        (await _repo.GetItemByIdAsync(Tenant, "camp", "item"))!.Decision.ShouldBe(RecertDecision.Pending);
    }

    [Fact]
    public async Task TryUpdateItemDecision_SecondDecision_LosesTheRace()
    {
        await SeedAsync();

        (await _repo.TryUpdateItemDecisionAsync(
            Tenant, "camp", "item", RecertDecision.Pending, RecertDecision.Certified, "bob", null)).ShouldBeTrue();
        (await _repo.TryUpdateItemDecisionAsync(
            Tenant, "camp", "item", RecertDecision.Pending, RecertDecision.Revoked, "carol", null)).ShouldBeFalse();

        var item = (await _repo.GetItemByIdAsync(Tenant, "camp", "item"))!;
        item.Decision.ShouldBe(RecertDecision.Certified);
        item.DecisionBy.ShouldBe("bob");
    }
}
