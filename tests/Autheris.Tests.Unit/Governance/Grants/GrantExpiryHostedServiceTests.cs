#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Governance.Grants;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Grants;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GrantExpiryHostedServiceTests
{
    private static readonly TenantId Tenant = new("tenant-expiry");
    private static readonly TableIdentifier Table = new("warehouse", "dbo", "customers");
    private static readonly DateTimeOffset BaseTime = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExpiryNotice_SentOnce_BeforeLeadTime()
    {
        var clock = new FakeTimeProvider(BaseTime.AddDays(-31));
        var consent = BuildConsent(validFrom: BaseTime.AddDays(-10), validTo: BaseTime);
        var candidate = new ExpiringGrantCandidate(consent, ConsentWorkflowMode.Internal, null);

        DateTimeOffset? sentAt = null;
        var store = Substitute.For<IGrantExpiryStore>();
        store.GetGrantsWithPendingReminderAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => sentAt.HasValue ? [] : [candidate]);
        store.MarkReminderStageSentAsync(candidate, Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (sentAt.HasValue) return false;
                sentAt = (DateTimeOffset)ci[2];
                return true;
            });
        store.GetExpiredGrantsWithoutRecordAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        store.GetOpenPendingRequestsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);

        var router = Substitute.For<IConsentWorkflowRouter>();
        router.ResolveForTableAsync(Tenant, Table, Arg.Any<CancellationToken>())
            .Returns(new ConsentWorkflowRouteResolution(ConsentWorkflowMode.Internal, null, 60, 30, true, WorkflowRouteScopeKind.Tenant));

        var service = new GrantExpiryHostedService(store, router, NullLogger<GrantExpiryHostedService>.Instance, timeProvider: clock);

        // Before the first default reminder (31 days remaining vs 30): no notice yet
        var first = await service.RunSweepAsync();
        first.NoticesSent.ShouldBe(0);

        // Inside the first reminder window (29 days remaining): stage 1 sent once
        clock.Advance(TimeSpan.FromDays(2));
        var second = await service.RunSweepAsync();
        second.NoticesSent.ShouldBe(1);
        sentAt.ShouldBe(clock.GetUtcNow());

        // Subsequent sweep does not re-emit
        var third = await service.RunSweepAsync();
        third.NoticesSent.ShouldBe(0);
    }

    [Fact]
    public async Task ExpiredGrant_WritesExpiredRecord()
    {
        var clock = new FakeTimeProvider(BaseTime);
        var consent = BuildConsent(validFrom: BaseTime.AddDays(-7), validTo: BaseTime.AddMinutes(-5));
        var candidate = new ExpiringGrantCandidate(consent, ConsentWorkflowMode.External, Guid.NewGuid());

        var store = Substitute.For<IGrantExpiryStore>();
        store.GetGrantsWithPendingReminderAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        store.GetExpiredGrantsWithoutRecordAsync(BaseTime, Arg.Any<CancellationToken>()).Returns([candidate]);
        store.RecordGrantExpiryAsync(candidate, BaseTime, Arg.Any<CancellationToken>()).Returns(true);
        store.GetOpenPendingRequestsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);

        var router = Substitute.For<IConsentWorkflowRouter>();
        var service = new GrantExpiryHostedService(store, router, NullLogger<GrantExpiryHostedService>.Instance, timeProvider: clock);

        var summary = await service.RunSweepAsync();

        summary.GrantsExpired.ShouldBe(1);
        await store.Received(1).RecordGrantExpiryAsync(candidate, BaseTime, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PendingRequest_TimesOut()
    {
        var clock = new FakeTimeProvider(BaseTime);
        var staleRequest = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = Table,
            TenantId = Tenant,
            RequesterSid = new Sid("S-1-5-21-1001"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-1001",
            BusinessJustification = "Reporting query",
            Status = "PENDING",
            RequestedAt = BaseTime.AddDays(-15),
            RequestedValidTo = BaseTime.AddDays(30)
        };
        var freshRequest = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = Table,
            TenantId = Tenant,
            RequesterSid = new Sid("S-1-5-21-1002"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-1002",
            BusinessJustification = "Recent request",
            Status = "PENDING_EXTERNAL_APPROVAL",
            RequestedAt = BaseTime.AddDays(-5),
            RequestedValidTo = BaseTime.AddDays(30)
        };

        var store = Substitute.For<IGrantExpiryStore>();
        store.GetGrantsWithPendingReminderAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        store.GetExpiredGrantsWithoutRecordAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        store.GetOpenPendingRequestsAsync(BaseTime, Arg.Any<CancellationToken>()).Returns([staleRequest, freshRequest]);
        store.ExpirePendingRequestAsync(staleRequest, BaseTime, Arg.Any<CancellationToken>()).Returns(true);

        var router = Substitute.For<IConsentWorkflowRouter>();
        router.ResolveForTableAsync(Tenant, Table, Arg.Any<CancellationToken>())
            .Returns(new ConsentWorkflowRouteResolution(ConsentWorkflowMode.Internal, null, 60, RequestTimeoutDays: 14, RequireResolvableApprover: true, MatchedScopeKind: WorkflowRouteScopeKind.Tenant));

        var service = new GrantExpiryHostedService(store, router, NullLogger<GrantExpiryHostedService>.Instance, timeProvider: clock);

        var summary = await service.RunSweepAsync();

        summary.RequestsTimedOut.ShouldBe(1);
        await store.Received(1).ExpirePendingRequestAsync(staleRequest, BaseTime, Arg.Any<CancellationToken>());
        await store.DidNotReceive().ExpirePendingRequestAsync(freshRequest, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepWithLockAsync_WhenLockBusy_ReturnsNullWithoutTouchingStore()
    {
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.TryAcquireLockAsync(GrantExpiryHostedService.ScanLockKey, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>((IAsyncDisposable?)null));

        var store = Substitute.For<IGrantExpiryStore>();
        var router = Substitute.For<IConsentWorkflowRouter>();
        var service = new GrantExpiryHostedService(
            store,
            router,
            NullLogger<GrantExpiryHostedService>.Instance,
            clusterState,
            new FakeTimeProvider(BaseTime));

        var result = await service.RunSweepWithLockAsync(CancellationToken.None);

        result.ShouldBeNull();
        await store.DidNotReceive().GetGrantsWithPendingReminderAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static Consent BuildConsent(DateTimeOffset validFrom, DateTimeOffset validTo) => new()
    {
        Id = Guid.NewGuid(),
        TableId = Guid.NewGuid(),
        TableIdentifier = Table,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("S-1-5-21-1001"),
        CreatedBySid = new Sid("S-1-5-21-2001"),
        ValidFrom = validFrom,
        ValidTo = validTo
    };
}
