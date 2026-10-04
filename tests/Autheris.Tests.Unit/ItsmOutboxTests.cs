namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Workflows;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Itsm;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class ItsmOutboxTests
{
    private sealed class StubEpochValidationService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(1L);

        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }

    private sealed class StubItsmWorkflowClient : IItsmWorkflowClient
    {
        public ItsmSystemType SystemType => ItsmSystemType.ServiceNow;
        public bool ShouldFail { get; set; }

        public Task<ItsmTicketResult> CreateAccessTicketAsync(ItsmTicketRequest request, CancellationToken ct = default)
        {
            if (ShouldFail)
            {
                return Task.FromResult(new ItsmTicketResult(false, null, "NET_ERR", "Network timeout contacting ServiceNow"));
            }

            var ticketRef = new ItsmTicketReference(ItsmSystemType.ServiceNow, "RITM0019283", "https://servicenow.corp/RITM0019283");
            return Task.FromResult(new ItsmTicketResult(true, ticketRef, null, null));
        }
    }

    [Fact]
    public async Task OutboxRepository_ShouldEnqueueAndRetrievePendingMessages()
    {
        // Arrange
        using var repo = new SqliteGovernanceRepository(new StubEpochValidationService());
        var msg = new ItsmOutboxMessage(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            "tenant-alpha",
            "CreateAccessTicket",
            "{}",
            "ServiceNow",
            ItsmOutboxStatus.Pending,
            0,
            5,
            DateTimeOffset.UtcNow);

        // Act
        await repo.EnqueueAsync(msg);
        var pending = await repo.GetPendingMessagesAsync(10);

        // Assert
        pending.ShouldNotBeNull();
        pending.Count.ShouldBe(1);
        pending[0].Id.ShouldBe(msg.Id);
        pending[0].TenantId.ShouldBe("tenant-alpha");
        pending[0].Status.ShouldBe(ItsmOutboxStatus.Pending);

        // Mark Completed
        await repo.MarkCompletedAsync(msg.Id, "TICKET-123");
        var afterComplete = await repo.GetPendingMessagesAsync(10);
        afterComplete.Count.ShouldBe(0);
    }

    [Fact]
    public async Task OutboxRepository_MarkFailed_ShouldBackoffAndDeadLetterWhenMaxRetriesExceeded()
    {
        // Arrange
        using var repo = new SqliteGovernanceRepository(new StubEpochValidationService());
        var msg = new ItsmOutboxMessage(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            "tenant-beta",
            "CreateAccessTicket",
            "{}",
            "Jira",
            ItsmOutboxStatus.Pending,
            2,
            3,
            DateTimeOffset.UtcNow);

        await repo.EnqueueAsync(msg);

        // Act - Mark failed with dead letter
        await repo.MarkFailedAsync(msg.Id, "Connection refused", TimeSpan.FromMinutes(1), deadLetter: true);

        var pending = await repo.GetPendingMessagesAsync(10);
        var deadLetter = await repo.GetDeadLetterMessagesAsync(10);

        // Assert
        pending.Count.ShouldBe(0);
        deadLetter.Count.ShouldBe(1);
        deadLetter[0].Id.ShouldBe(msg.Id);
        deadLetter[0].Status.ShouldBe(ItsmOutboxStatus.DeadLetter);
        deadLetter[0].LastError.ShouldBe("Connection refused");
        deadLetter[0].RetryCount.ShouldBe(3);
    }

    [Fact]
    public async Task ItsmOutboxDispatcherHostedService_ShouldProcessPendingMessageSuccessfully()
    {
        // Arrange
        var services = new ServiceCollection();
        var clientStub = new StubItsmWorkflowClient();

        using var repo = new SqliteGovernanceRepository(new StubEpochValidationService());
        services.AddSingleton<IItsmOutboxRepository>(repo);
        services.AddSingleton<IConsentApprovalRepository>(repo);
        services.AddSingleton<IItsmWorkflowClient>(clientStub);
        services.AddSingleton<ItsmWorkflowDispatcher>(sp => new ItsmWorkflowDispatcher(
            sp.GetServices<IItsmWorkflowClient>(),
            NullLogger<ItsmWorkflowDispatcher>.Instance));

        var sp = services.BuildServiceProvider();
        var dispatcherService = new ItsmOutboxDispatcherHostedService(sp, NullLogger<ItsmOutboxDispatcherHostedService>.Instance);

        var ticketReq = new ItsmTicketRequest(
            new TenantId("tenant-alpha"),
            new Sid("S-1-5-21-123"),
            new TableIdentifier("sales", "crm", "customers"),
            "Need access for report",
            30,
            null,
            null);

        var reqId = Guid.NewGuid();
        var msg = new ItsmOutboxMessage(
            Guid.NewGuid().ToString(),
            reqId.ToString(),
            "tenant-alpha",
            "CreateAccessTicket",
            JsonSerializer.Serialize(ticketReq),
            "ServiceNow",
            ItsmOutboxStatus.Pending,
            0,
            5,
            DateTimeOffset.UtcNow);

        await repo.EnqueueAsync(msg);

        // Act
        var processed = await dispatcherService.ProcessPendingMessagesAsync(CancellationToken.None);

        // Assert
        processed.ShouldBe(1);
        var pendingAfter = await repo.GetPendingMessagesAsync(10);
        pendingAfter.Count.ShouldBe(0);
    }
}
