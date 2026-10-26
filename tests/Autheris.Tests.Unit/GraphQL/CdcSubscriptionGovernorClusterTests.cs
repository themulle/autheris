namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Subscriptions;
using HotChocolate;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CdcSubscriptionGovernorClusterTests
{
    private static ClaimsPrincipal CreateUser(string sid, string tenant = "tenant-a", params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", tenant),
            new("exp", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString())
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static (CdcSubscriptionGovernor Governor, IAuditLogRepository Audit, ITableAccessResolver Resolver) CreateGovernor(
        IDistributedClusterStateProvider clusterState,
        int maxPerSubject = 2,
        int maxPerTenant = 3)
    {
        var options = Options.Create(new GatewayOptions
        {
            GraphQL = new GraphQLOptions
            {
                MaxSubscriptionsPerSubject = maxPerSubject,
                MaxSubscriptionsPerTenant = maxPerTenant
            }
        });

        var resolver = Substitute.For<ITableAccessResolver>();
        resolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var table = ci.ArgAt<TableIdentifier>(1);
                var decision = TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
                return Task.FromResult(new ResolvedTableAccess(new TableMetadata { Identifier = table }, decision, new TenantId("tenant-a"), new Sid("S-1-USER"), ci.ArgAt<ClaimsPrincipal?>(0)!));
            });

        var audit = Substitute.For<IAuditLogRepository>();
        var governor = new CdcSubscriptionGovernor(options, NullLogger<CdcSubscriptionGovernor>.Instance, clusterState);

        return (governor, audit, resolver);
    }

    [Fact]
    public async Task Cluster_OneSubjectAtLimit_OtherSubjectOfSameTenantAdmitted()
    {
        var clusterState = new InMemoryClusterStateProvider();
        var (governor, audit, resolver) = CreateGovernor(clusterState, maxPerSubject: 2, maxPerTenant: 10);

        var user1 = CreateUser("S-1-USER1");
        var user2 = CreateUser("S-1-USER2");

        var table = new TableIdentifier("default", "public", "orders");

        // User 1 admits 2 subscriptions
        await using var l1 = await governor.AdmitAsync(user1, "cdc_orders", table, resolver, audit, CancellationToken.None);
        await using var l2 = await governor.AdmitAsync(user1, "cdc_orders", table, resolver, audit, CancellationToken.None);

        // User 1 third attempt reaches per-subject limit
        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            governor.AdmitAsync(user1, "cdc_orders", table, resolver, audit, CancellationToken.None));
        ex.Errors[0].Code.ShouldBe("TOO_MANY_REQUESTS");

        // User 2 from the SAME tenant is admitted
        await using var l3 = await governor.AdmitAsync(user2, "cdc_orders", table, resolver, audit, CancellationToken.None);
        l3.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cluster_TenantCapExceeded_Denied()
    {
        var clusterState = new InMemoryClusterStateProvider();
        var (governor, audit, resolver) = CreateGovernor(clusterState, maxPerSubject: 5, maxPerTenant: 3);

        var table = new TableIdentifier("default", "public", "orders");

        await using var l1 = await governor.AdmitAsync(CreateUser("S-1-U1"), "cdc_orders", table, resolver, audit, CancellationToken.None);
        await using var l2 = await governor.AdmitAsync(CreateUser("S-1-U2"), "cdc_orders", table, resolver, audit, CancellationToken.None);
        await using var l3 = await governor.AdmitAsync(CreateUser("S-1-U3"), "cdc_orders", table, resolver, audit, CancellationToken.None);

        // Fourth across tenant exceeds MaxSubscriptionsPerTenant (3)
        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            governor.AdmitAsync(CreateUser("S-1-U4"), "cdc_orders", table, resolver, audit, CancellationToken.None));
        ex.Errors[0].Code.ShouldBe("TOO_MANY_REQUESTS");
    }

    [Fact]
    public async Task Cluster_SecondSlotFails_FirstReleased()
    {
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();

        // Slot 1 (subject) succeeds
        clusterState.TryAcquireSlotAsync(Arg.Is<string>(k => k.Contains("S-1-USER")), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool?>(true));

        // Slot 2 (tenant cap) fails
        clusterState.TryAcquireSlotAsync(Arg.Is<string>(k => !k.Contains("S-1-USER")), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool?>(false));

        var (governor, audit, resolver) = CreateGovernor(clusterState, maxPerSubject: 5, maxPerTenant: 5);
        var table = new TableIdentifier("default", "public", "orders");

        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            governor.AdmitAsync(CreateUser("S-1-USER"), "cdc_orders", table, resolver, audit, CancellationToken.None));
        ex.Errors[0].Code.ShouldBe("TOO_MANY_REQUESTS");

        // Verify slot 1 was released!
        await clusterState.Received(1).ReleaseSlotAsync(
            Arg.Is<string>(k => k.Contains("S-1-USER")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cluster_ExpiredSlotOfCrashedInstance_NoLongerCounted()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var clusterState = new InMemoryClusterStateProvider(time);
        const string setKey = "cdc:subs:tenant-test";

        // Crashed instance acquired a slot with a very short TTL (50 ms)
        var acquired = await clusterState.TryAcquireSlotAsync(setKey, "crashed-guid", 1, TimeSpan.FromMilliseconds(50));
        acquired.ShouldBe(true);

        // Immediately, limit of 1 prevents another acquire
        var blocked = await clusterState.TryAcquireSlotAsync(setKey, "other-guid", 1, TimeSpan.FromMinutes(2));
        blocked.ShouldBe(false);

        // Wait for crashed instance slot to expire
        time.Advance(TimeSpan.FromMilliseconds(120));

        // New instance can now acquire slot; expired slot is pruned and no longer counted
        var newAcquire = await clusterState.TryAcquireSlotAsync(setKey, "new-guid", 1, TimeSpan.FromMinutes(2));
        newAcquire.ShouldBe(true);
    }

    [Fact]
    public async Task Lease_RenewalFails_StreamCompleted()
    {
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.TryAcquireSlotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool?>(true));

        // Renewal returns false (expired / lost)
        clusterState.RenewSlotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(false));

        var (governor, audit, resolver) = CreateGovernor(clusterState);
        var table = new TableIdentifier("default", "public", "orders");
        using var streamCts = new CancellationTokenSource();

        await using var lease = await governor.AdmitAsync(CreateUser("S-1-USER"), "cdc_orders", table, resolver, audit, CancellationToken.None, streamCts);
        lease.IsStreamCompleted.ShouldBeFalse();

        // Trigger renewal
        var success = await lease.TriggerRenewalAsync();
        success.ShouldBeFalse();

        // Fails closed: stream CTS is cancelled
        lease.IsStreamCompleted.ShouldBeTrue();
        streamCts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Redis_TryAcquireSlot_ConcurrentAcquires_RespectLimit()
    {
        var clusterState = new InMemoryClusterStateProvider();
        const string setKey = "test:slots:concurrent";
        const int limit = 5;
        const int totalCallers = 25;

        var tasks = Enumerable.Range(0, totalCallers).Select(async i =>
        {
            await Task.Yield();
            return await clusterState.TryAcquireSlotAsync(setKey, $"member-{i}", limit, TimeSpan.FromMinutes(2));
        });

        var results = await Task.WhenAll(tasks);

        results.Count(r => r == true).ShouldBe(limit);
        results.Count(r => r == false).ShouldBe(totalCallers - limit);
    }
}
