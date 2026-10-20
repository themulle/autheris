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
    private readonly IAccessPolicyRepository _accessPolicyRepo = Substitute.For<IAccessPolicyRepository>();
    private readonly IPolicyEpochService _epochService = Substitute.For<IPolicyEpochService>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly RecertificationService _sut;

    public RecertificationCampaignTests()
    {
        _sut = new RecertificationService(_repository, _functionalRightRepo, _accessPolicyRepo, _epochService, _auditWriter);
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
        await _repository.Received(1).UpdateItemDecisionAsync(
            tenantId, campaignId, itemId, RecertDecision.Revoked, "SYSTEM_AUTO_REVOKE",
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Verify underlying asset mapping was removed
        await _functionalRightRepo.Received(1).DeleteMappingAsync(
            tenantId, "workforce-entra", "engineers", new FunctionalRight("Data.Read"), Arg.Any<CancellationToken>());

        // Verify audit log recorded
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "SECURITY_RECERT_AUTO_REVOKED" && e.Decision == "DENY"),
            Arg.Any<CancellationToken>());
    }
}
