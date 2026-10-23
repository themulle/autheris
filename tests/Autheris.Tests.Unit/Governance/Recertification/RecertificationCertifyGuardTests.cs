namespace Autheris.Tests.Unit.Governance.Recertification;

using System;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Recertification;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>GOV-02: certify/revoke enforce the designated reviewer, the pending state and the campaign state.</summary>
[Trait("Category", "Security")]
public sealed class RecertificationCertifyGuardTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private const string Key = "workforce-entra:engineers:Data.Read";

    private readonly IRecertificationRepository _repository = Substitute.For<IRecertificationRepository>();
    private readonly IFunctionalRightRepository _rights = Substitute.For<IFunctionalRightRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly RecertificationService _sut;

    public RecertificationCertifyGuardTests()
    {
        _sut = new RecertificationService(_repository, _rights, null, _audit, null, _time);
        _repository.TryUpdateItemDecisionAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecertDecision>(), Arg.Any<RecertDecision>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private void Seed(
        RecertDecision decision = RecertDecision.Pending,
        RecertCampaignStatus status = RecertCampaignStatus.Active,
        TimeSpan? deadlineFromNow = null)
    {
        var item = new RecertificationItem("tenant-1", "camp", "item", "FunctionalRightMapping", Key,
            AssignedTo: "user-alice", ReviewerId: "user-bob", Decision: decision);
        _repository.GetItemByIdAsync(Tenant, "camp", "item", Arg.Any<CancellationToken>()).Returns(item);
        _repository.GetCampaignByIdAsync(Tenant, "camp", Arg.Any<CancellationToken>()).Returns(
            new RecertificationCampaign("tenant-1", "camp", "c", RecertClass.Standard, status,
                _time.GetUtcNow() + (deadlineFromNow ?? TimeSpan.FromDays(1)), _time.GetUtcNow()));
    }

    private async Task AssertNothingWrittenAsync()
    {
        await _repository.DidNotReceiveWithAnyArgs().TryUpdateItemDecisionAsync(default, default!, default!, default, default, default!, default, default);
        await _rights.DidNotReceiveWithAnyArgs().DeleteMappingAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task Certify_ByNonDesignatedReviewer_Throws()
    {
        Seed();

        await Should.ThrowAsync<SecurityException>(() => _sut.CertifyItemAsync(Tenant, "camp", "item", "user-mallory"));

        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Certify_DesignatedReviewer_Succeeds_WithCompareAndSetFromPending()
    {
        Seed();

        await _sut.CertifyItemAsync(Tenant, "camp", "item", "user-bob");

        await _repository.Received(1).TryUpdateItemDecisionAsync(
            Tenant, "camp", "item", RecertDecision.Pending, RecertDecision.Certified, "user-bob", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Certify_RevokedItem_Throws()
    {
        Seed(RecertDecision.Revoked);

        await Should.ThrowAsync<RecertificationConflictException>(() => _sut.CertifyItemAsync(Tenant, "camp", "item", "user-bob"));

        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Certify_AfterDeadline_Throws()
    {
        Seed(deadlineFromNow: TimeSpan.FromSeconds(-1));

        await Should.ThrowAsync<RecertificationConflictException>(() => _sut.CertifyItemAsync(Tenant, "camp", "item", "user-bob"));

        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Certify_CompletedCampaign_Throws()
    {
        Seed(status: RecertCampaignStatus.Completed);

        await Should.ThrowAsync<RecertificationConflictException>(() => _sut.CertifyItemAsync(Tenant, "camp", "item", "user-bob"));

        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Certify_MissingCampaign_Throws()
    {
        Seed();
        _repository.GetCampaignByIdAsync(Tenant, "camp", Arg.Any<CancellationToken>()).Returns((RecertificationCampaign?)null);

        await Should.ThrowAsync<System.Collections.Generic.KeyNotFoundException>(() => _sut.CertifyItemAsync(Tenant, "camp", "item", "user-bob"));

        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Certify_WhenCompareAndSetLoses_ThrowsConflict()
    {
        Seed();
        _repository.TryUpdateItemDecisionAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecertDecision>(), Arg.Any<RecertDecision>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var ex = await Should.ThrowAsync<RecertificationConflictException>(() => _sut.CertifyItemAsync(Tenant, "camp", "item", "user-bob"));

        ex.Code.ShouldBe("RECERT_ITEM_ALREADY_DECIDED");
    }

    [Fact]
    public async Task Revoke_ByNonDesignatedReviewer_Throws()
    {
        Seed();

        await Should.ThrowAsync<SecurityException>(() => _sut.RevokeItemAsync(Tenant, "camp", "item", "user-mallory"));

        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AutoRevoke_ConcurrentCertify_EndsRevokedAndAudited()
    {
        var item = new RecertificationItem("tenant-1", "camp", "item", "FunctionalRightMapping", Key,
            AssignedTo: "user-alice", ReviewerId: "user-bob", Decision: RecertDecision.Pending);
        _repository.GetPendingItemsPastDeadlineAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([item]);

        // The reviewer certified between the pending read and the auto-revoke write.
        _repository.TryUpdateItemDecisionAsync(Tenant, "camp", "item", RecertDecision.Pending, RecertDecision.Revoked, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _repository.GetItemByIdAsync(Tenant, "camp", "item", Arg.Any<CancellationToken>())
            .Returns(item with { Decision = RecertDecision.Certified, DecisionBy = "user-bob" });

        var count = await _sut.ExecuteAutoRevocationCycleAsync(_time.GetUtcNow());

        count.ShouldBe(1);
        await _rights.Received(1).DeleteMappingAsync(Tenant, "workforce-entra", "engineers", new FunctionalRight("Data.Read"), Arg.Any<CancellationToken>());
        await _repository.Received(1).TryUpdateItemDecisionAsync(
            Tenant, "camp", "item", RecertDecision.Certified, RecertDecision.Revoked, "SYSTEM_AUTO_REVOKE", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "SECURITY_RECERT_DECISION_CONFLICT"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoRevokeCycle_TenantScoped_OnlyRevokesItemsOfThatTenant_GOV03()
    {
        var own = new RecertificationItem("tenant-1", "camp", "item-own", "FunctionalRightMapping", Key,
            AssignedTo: "user-alice", ReviewerId: "user-bob", Decision: RecertDecision.Pending);
        var foreign = own with { TenantId = "tenant-2", ItemId = "item-foreign" };
        _repository.GetPendingItemsPastDeadlineAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([foreign, own]);

        var count = await _sut.ExecuteAutoRevocationCycleAsync(Tenant, _time.GetUtcNow());

        count.ShouldBe(1);
        await _repository.DidNotReceive().TryUpdateItemDecisionAsync(
            new TenantId("tenant-2"), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecertDecision>(), Arg.Any<RecertDecision>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
