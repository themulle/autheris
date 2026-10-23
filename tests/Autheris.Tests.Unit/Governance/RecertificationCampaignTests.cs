namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Policies;
using Autheris.Application.Governance.Recertification;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class RecertificationCampaignTests
{
    private readonly IRecertificationRepository _repository = Substitute.For<IRecertificationRepository>();
    private readonly IFunctionalRightRepository _functionalRightRepo = Substitute.For<IFunctionalRightRepository>();
    private readonly IPolicyEpochService _epochService = Substitute.For<IPolicyEpochService>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly RecertificationService _sut;

    public RecertificationCampaignTests()
    {
        _sut = new RecertificationService(_repository, _functionalRightRepo, _epochService, _auditWriter);
        _repository.GetCampaignByIdAsync(Arg.Any<TenantId>(), "camp-1", Arg.Any<CancellationToken>())
            .Returns(new RecertificationCampaign("tenant-1", "camp-1", "c", RecertClass.Standard,
                RecertCampaignStatus.Active, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow));
        _repository.TryUpdateItemDecisionAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecertDecision>(), Arg.Any<RecertDecision>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [Fact]
    public async Task CreateCampaign_WithAccessPolicyAssetType_IsRejected()
    {
        var tenantId = new TenantId("tenant-1");
        await Should.ThrowAsync<ArgumentException>(() =>
            _sut.CreateCampaignAsync(tenantId, "Campaign", RecertClass.Standard, assetType: "AccessPolicy"));
    }

    [Fact]
    public async Task AddItem_WithAccessPolicyAssetType_IsRejected()
    {
        var tenantId = new TenantId("tenant-1");
        await Should.ThrowAsync<ArgumentException>(() =>
            _sut.AddItemAsync(tenantId, "camp-1", "AccessPolicy", "key-1", "user-1", "reviewer-1"));
    }

    [Fact]
    public async Task RecertificationService_AntiSelfReview_ThrowsOnSelfCertification()
    {
        var tenantId = new TenantId("tenant-1");
        var campaignId = "camp-1";
        var itemId = "item-1";
        var assignedTo = "user-alice";

        var item = new RecertificationItem(
            tenantId.Value,
            campaignId,
            itemId,
            AssetType: "FunctionalRightMapping",
            AssetKey: "workforce-entra:engineers:Data.Read",
            AssignedTo: assignedTo,
            ReviewerId: "user-bob",
            Decision: RecertDecision.Pending
        );

        _repository.GetItemByIdAsync(tenantId, campaignId, itemId, Arg.Any<CancellationToken>())
            .Returns(item);

        // Attempting to self-certify: reviewerId == assignedTo ("user-alice")
        var act = () => _sut.CertifyItemAsync(tenantId, campaignId, itemId, reviewerId: "user-alice");

        await act.ShouldThrowAsync<SecurityException>();
    }

    [Fact]
    public async Task RecertificationService_AutoRevocation_DeactivatesAndAudits()
    {
        var tenantId = new TenantId("tenant-1");
        var campaignId = "camp-1";
        var itemId = "item-expired";
        var asOf = DateTimeOffset.UtcNow;

        var expiredItem = new RecertificationItem(
            tenantId.Value,
            campaignId,
            itemId,
            AssetType: "FunctionalRightMapping",
            AssetKey: "workforce-entra:engineers:Data.Read",
            AssignedTo: "user-charlie",
            ReviewerId: "user-bob",
            Decision: RecertDecision.Pending
        );

        _repository.GetPendingItemsPastDeadlineAsync(asOf, Arg.Any<CancellationToken>())
            .Returns([expiredItem]);

        var revokedCount = await _sut.ExecuteAutoRevocationCycleAsync(asOf);

        revokedCount.ShouldBe(1);

        // Verify item was updated to Revoked with SYSTEM_AUTO_REVOKE
        await _repository.Received(1).TryUpdateItemDecisionAsync(
            tenantId, campaignId, itemId, RecertDecision.Pending, RecertDecision.Revoked, "SYSTEM_AUTO_REVOKE",
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Verify underlying asset mapping was removed
        await _functionalRightRepo.Received(1).DeleteMappingAsync(
            tenantId, "workforce-entra", "engineers", new FunctionalRight("Data.Read"), Arg.Any<CancellationToken>());

        // Verify audit log recorded
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "SECURITY_RECERT_AUTO_REVOKED" && e.Decision == "DENY"),
            Arg.Any<CancellationToken>());
    }

    private static RecertificationItem Item(string itemId, string assetType, string assetKey) => new(
        "tenant-1", "camp-1", itemId, AssetType: assetType, AssetKey: assetKey,
        AssignedTo: "user-charlie", ReviewerId: "user-bob", Decision: RecertDecision.Pending);

    [Fact]
    public async Task RevokeItem_WhenDeactivationThrows_DoesNotMarkTheItemRevoked()
    {
        var tenantId = new TenantId("tenant-1");
        var item = Item("item-1", "FunctionalRightMapping", "workforce-entra:engineers:Data.Read");
        _repository.GetItemByIdAsync(tenantId, "camp-1", "item-1", Arg.Any<CancellationToken>()).Returns(item);
        _functionalRightRepo.DeleteMappingAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FunctionalRight>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("store down")));

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.RevokeItemAsync(tenantId, "camp-1", "item-1", "user-bob"));

        await _repository.DidNotReceiveWithAnyArgs().TryUpdateItemDecisionAsync(default, default!, default!, default, default, default!, default, default);
    }

    [Fact]
    public async Task RevokeItem_DeactivatesBeforeRecordingTheDecision()
    {
        var tenantId = new TenantId("tenant-1");
        var item = Item("item-1", "FunctionalRightMapping", "workforce-entra:engineers:Data.Read");
        _repository.GetItemByIdAsync(tenantId, "camp-1", "item-1", Arg.Any<CancellationToken>()).Returns(item);
        var order = new List<string>();
        _functionalRightRepo.DeleteMappingAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FunctionalRight>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("deactivate"); return Task.CompletedTask; });
        _repository.TryUpdateItemDecisionAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecertDecision>(), Arg.Any<RecertDecision>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("decision"); return true; });

        await _sut.RevokeItemAsync(tenantId, "camp-1", "item-1", "user-bob");

        order.ShouldBe(new[] { "deactivate", "decision" });
    }

    [Fact]
    public async Task RevokeItem_MalformedFunctionalRightKey_FailsClosed_AndIsNotMarkedRevoked()
    {
        var tenantId = new TenantId("tenant-1");
        var item = Item("item-1", "FunctionalRightMapping", "malformed-key");
        _repository.GetItemByIdAsync(tenantId, "camp-1", "item-1", Arg.Any<CancellationToken>()).Returns(item);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.RevokeItemAsync(tenantId, "camp-1", "item-1", "user-bob"));

        await _repository.DidNotReceiveWithAnyArgs().TryUpdateItemDecisionAsync(default, default!, default!, default, default, default!, default, default);
    }

    [Fact]
    public async Task RevokeItem_UnknownAssetType_ThrowsNotSupported_AndIsNotMarkedRevoked()
    {
        var tenantId = new TenantId("tenant-1");
        var item = Item("item-1", "UnknownAssetType", "whatever");
        _repository.GetItemByIdAsync(tenantId, "camp-1", "item-1", Arg.Any<CancellationToken>()).Returns(item);

        await Should.ThrowAsync<NotSupportedException>(() => _sut.RevokeItemAsync(tenantId, "camp-1", "item-1", "user-bob"));

        await _repository.DidNotReceiveWithAnyArgs().TryUpdateItemDecisionAsync(default, default!, default!, default, default, default!, default, default);
    }

    [Fact]
    public async Task AutoRevocation_IsolatesFailingItems_AndKeepsThemPending()
    {
        var asOf = DateTimeOffset.UtcNow;
        var failing = Item("item-fail", "FunctionalRightMapping", "workforce-entra:failing:Data.Read");
        var unknown = Item("item-unknown", "UnknownAssetType", "x");
        var ok = Item("item-ok", "FunctionalRightMapping", "workforce-entra:engineers:Data.Read");
        _repository.GetPendingItemsPastDeadlineAsync(asOf, Arg.Any<CancellationToken>()).Returns([failing, unknown, ok]);
        _functionalRightRepo.DeleteMappingAsync(Arg.Any<TenantId>(), "workforce-entra", "failing", Arg.Any<FunctionalRight>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("store down")));

        var revokedCount = await _sut.ExecuteAutoRevocationCycleAsync(asOf);

        revokedCount.ShouldBe(1);
        await _repository.Received(1).TryUpdateItemDecisionAsync(
            Arg.Any<TenantId>(), "camp-1", "item-ok", RecertDecision.Pending, RecertDecision.Revoked, "SYSTEM_AUTO_REVOKE", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().TryUpdateItemDecisionAsync(
            Arg.Any<TenantId>(), Arg.Any<string>(), "item-fail", Arg.Any<RecertDecision>(), Arg.Any<RecertDecision>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().TryUpdateItemDecisionAsync(
            Arg.Any<TenantId>(), Arg.Any<string>(), "item-unknown", Arg.Any<RecertDecision>(), Arg.Any<RecertDecision>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }
}
