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
}
