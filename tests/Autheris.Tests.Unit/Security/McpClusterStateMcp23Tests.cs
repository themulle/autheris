namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// MCP-2: session store and HitL approvals never block on the cluster state (no sync-over-async, nothing awaited inside a
/// lock). MCP-3: the cross-node SSE subscription of a session is released when the session ends.
/// </summary>
public sealed class McpClusterStateMcp23Tests
{
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(2);

    /// <summary>Cluster state whose writes never complete (an unresponsive Redis).</summary>
    private static (IDistributedClusterStateProvider State, TaskCompletionSource Never) HangingClusterState()
    {
        var never = new TaskCompletionSource();
        var state = Substitute.For<IDistributedClusterStateProvider>();
        state.SetAsync(Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask(never.Task));
        state.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(never.Task.ContinueWith(_ => true)));
        return (state, never);
    }

    [Fact]
    public async Task SessionStore_WithUnresponsiveClusterState_DoesNotBlock()
    {
        var (state, never) = HangingClusterState();
        var store = new McpSessionStore(NullLogger<McpSessionStore>.Instance, state);

        var work = Task.Run(() =>
        {
            var session = store.CreateSession("sp", "tenant-a", "S-1-5-21-U");
            store.GetSession(session.SessionId).ShouldNotBeNull();
            store.RefreshPrincipalContext(session.SessionId, ["Reader"], []).ShouldNotBeNull();
            store.RemoveSession(session.SessionId).ShouldBeTrue();
        });

        await work.WaitAsync(Fast);
        never.TrySetResult();
    }

    [Fact]
    public async Task SseSubscription_IsDisposed_WhenSessionIsRemoved()
    {
        var subscription = Substitute.For<IAsyncDisposable>();
        var state = Substitute.For<IDistributedClusterStateProvider>();
        state.SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<McpSsePayload, ValueTask>>(), Arg.Any<CancellationToken>()).Returns(subscription);
        var store = new McpSessionStore(NullLogger<McpSessionStore>.Instance, state);
        var session = store.CreateSession("sp", "tenant-a", "S-1-5-21-U");

        store.RegisterSseSender(session.SessionId, (_, _) => Task.CompletedTask);
        store.SseSubscriptionCount.ShouldBe(1);

        store.RemoveSession(session.SessionId);

        store.SseSubscriptionCount.ShouldBe(0);
        await Task.Delay(50);
        await subscription.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task SessionOfAnotherNode_IsFoundAsynchronously()
    {
        var remote = new McpSessionContext("remote-1", "sp", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-5-21-U");
        var state = Substitute.For<IDistributedClusterStateProvider>();
        state.GetAsync<McpSessionContext>("mcp:session:remote-1", Arg.Any<CancellationToken>()).Returns(new ValueTask<McpSessionContext?>(remote));
        var store = new McpSessionStore(NullLogger<McpSessionStore>.Instance, state);

        store.GetSession("remote-1").ShouldBeNull(); // local lookup never blocks on a remote read
        (await store.GetSessionAsync("remote-1")).ShouldNotBeNull();
    }

    [Fact]
    public async Task HitLApproval_WithBusyDistributedLock_ReturnsRetry_WithoutBlocking()
    {
        var state = Substitute.For<IDistributedClusterStateProvider>();
        state.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>((IAsyncDisposable?)null));
        var service = new HitLStepUpApprovalService(
            Options.Create(new GatewayOptions()), NullLogger<HitLStepUpApprovalService>.Instance, clusterState: state);

        var request = service.RequestStepUpApprovalAsync("tool", "tenant-a", "requester", new TableIdentifier("d", "s", "t"));
        await Task.Delay(50);
        var ticket = service.GetPendingTickets("tenant-a").ShouldHaveSingleItem();

        var result = await service.ApproveStepUpRequestAsync(ticket.ApprovalId, new HitLApproverContext("steward", ["steward"], "tenant-a")).WaitAsync(Fast);

        result.IsApproved.ShouldBeFalse();
        result.Message.ShouldNotBeNull().ShouldContain("another cluster node");
        await service.RejectStepUpRequestAsync(ticket.ApprovalId, new HitLApproverContext("steward", ["steward"], "tenant-a", IsCrossTenantAdmin: true)).WaitAsync(Fast);
        _ = request;
    }
}
