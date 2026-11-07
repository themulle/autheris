namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

public sealed class HitLClusterPartitionTests
{
    [Fact]
    public async Task RequestStepUp_WhenPartitionOccursAndFailClosedActive_ThrowsInvalidOperationException()
    {
        // Arrange
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.SetAsync(Arg.Any<string>(), Arg.Any<HitLApprovalTicket>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Redis cluster connection refused (partition)."));

        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions
            {
                Enabled = true,
                ApprovalTimeoutSeconds = 1,
                FailClosedOnClusterPartition = true
            }
        });

        var service = new HitLStepUpApprovalService(
            options,
            NullLogger<HitLStepUpApprovalService>.Instance,
            clusterState: clusterState);

        var tableId = new TableIdentifier("sales", "dbo", "orders");

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await service.RequestStepUpApprovalAsync(
                "query_sql",
                "tenant-a",
                "user:alice",
                tableId);
        });

        ex.Message.ShouldContain("fail-closed policy active");
    }

    [Fact]
    public async Task RequestStepUp_WhenPartitionOccursAndFailClosedDisabled_ContinuesGracefully()
    {
        // Arrange
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.SetAsync(Arg.Any<string>(), Arg.Any<HitLApprovalTicket>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Redis cluster connection refused (partition)."));

        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions
            {
                Enabled = true,
                ApprovalTimeoutSeconds = 1,
                FailClosedOnClusterPartition = false
            }
        });

        var service = new HitLStepUpApprovalService(
            options,
            NullLogger<HitLStepUpApprovalService>.Instance,
            clusterState: clusterState);

        var tableId = new TableIdentifier("sales", "dbo", "orders");

        // Act: Should timeout instead of throwing on ticket creation
        var result = await service.RequestStepUpApprovalAsync(
            "query_sql",
            "tenant-a",
            "user:alice",
            tableId);

        // Assert
        result.ShouldNotBeNull();
        result.IsApproved.ShouldBeFalse();
        result.Ticket.Status.ShouldBe(HitLApprovalStatus.Expired);
    }

    private static (HitLStepUpApprovalService service, Func<HitLApprovalBroadcast, ValueTask> callback, HitLApprovalTicket ticket) SetupServiceWithBroadcast()
    {
        Func<HitLApprovalBroadcast, ValueTask>? callback = null;
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.SubscribeAsync<HitLApprovalBroadcast>(
            Arg.Any<string>(),
            Arg.Do<Func<HitLApprovalBroadcast, ValueTask>>(h => callback = h),
            Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());

        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions
            {
                Enabled = true,
                ApprovalTimeoutSeconds = 60,
                FailClosedOnClusterPartition = false
            }
        });

        var service = new HitLStepUpApprovalService(
            options,
            NullLogger<HitLStepUpApprovalService>.Instance,
            clusterState: clusterState);

        var tableId = new TableIdentifier("sales", "dbo", "orders");
        _ = service.RequestStepUpApprovalAsync("query_sql", "tenant-a", "user:alice", tableId);

        var ticket = service.GetPendingTickets("tenant-a")[0];
        return (service, callback!, ticket);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Broadcast_with_mismatched_approval_id_is_rejected()
    {
        var (service, callback, ticket) = SetupServiceWithBroadcast();
        var issuedAt = ticket.CreatedAt.AddSeconds(1);
        var broadcast = new HitLApprovalBroadcast("other-approval-id", "approver-sid", true, null, issuedAt);
        var signature = HitLStepUpApprovalService.ComputeBroadcastSignature(service.HmacKey, broadcast);
        var signedBroadcast = broadcast with { Signature = signature };

        await callback(signedBroadcast);

        var currentTicket = await service.GetTicketAsync(ticket.ApprovalId);
        currentTicket.ShouldNotBeNull();
        currentTicket.Status.ShouldBe(HitLApprovalStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Expired_broadcast_is_rejected()
    {
        var (service, callback, ticket) = SetupServiceWithBroadcast();
        var issuedAt = ticket.ExpiresAt.AddSeconds(5);
        var broadcast = new HitLApprovalBroadcast(ticket.ApprovalId, "approver-sid", true, null, issuedAt);
        var signature = HitLStepUpApprovalService.ComputeBroadcastSignature(service.HmacKey, broadcast);
        var signedBroadcast = broadcast with { Signature = signature };

        await callback(signedBroadcast);

        var currentTicket = await service.GetTicketAsync(ticket.ApprovalId);
        currentTicket.ShouldNotBeNull();
        currentTicket.Status.ShouldBe(HitLApprovalStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Broadcast_signature_covers_issued_at()
    {
        var (service, callback, ticket) = SetupServiceWithBroadcast();
        var originalIssuedAt = ticket.CreatedAt.AddSeconds(5);
        var originalBroadcast = new HitLApprovalBroadcast(ticket.ApprovalId, "approver-sid", true, null, originalIssuedAt);
        var signature = HitLStepUpApprovalService.ComputeBroadcastSignature(service.HmacKey, originalBroadcast);

        var tamperedBroadcast = originalBroadcast with { IssuedAt = ticket.CreatedAt.AddSeconds(10), Signature = signature };

        await callback(tamperedBroadcast);

        var currentTicket = await service.GetTicketAsync(ticket.ApprovalId);
        currentTicket.ShouldNotBeNull();
        currentTicket.Status.ShouldBe(HitLApprovalStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-05")]
    public async Task Ticket_signature_is_not_valid_broadcast_signature()
    {
        var (service, callback, ticket) = SetupServiceWithBroadcast();
        var issuedAt = ticket.CreatedAt.AddSeconds(1);
        var ticketSignature = HitLStepUpApprovalService.ComputeTicketSignature(service.HmacKey, ticket);
        var broadcast = new HitLApprovalBroadcast(ticket.ApprovalId, "approver-sid", true, null, issuedAt, Signature: ticketSignature);

        await callback(broadcast);

        var currentTicket = await service.GetTicketAsync(ticket.ApprovalId);
        currentTicket.ShouldNotBeNull();
        currentTicket.Status.ShouldBe(HitLApprovalStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-05")]
    public async Task Broadcast_issued_before_ticket_creation_is_rejected()
    {
        var (service, callback, ticket) = SetupServiceWithBroadcast();
        var issuedAt = ticket.CreatedAt.AddSeconds(-31);
        var broadcast = new HitLApprovalBroadcast(ticket.ApprovalId, "approver-sid", true, null, issuedAt);
        var signature = HitLStepUpApprovalService.ComputeBroadcastSignature(service.HmacKey, broadcast);
        var signedBroadcast = broadcast with { Signature = signature };

        await callback(signedBroadcast);

        var currentTicket = await service.GetTicketAsync(ticket.ApprovalId);
        currentTicket.ShouldNotBeNull();
        currentTicket.Status.ShouldBe(HitLApprovalStatus.Pending);
    }
}
