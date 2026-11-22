namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.State;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using StackExchange.Redis;
using Xunit;

[Trait("Category", "Security")]
public sealed class TotpReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replayed_code_rejected(bool useClusterStore)
    {
        var cluster = useClusterStore ? new SpyClusterStateProvider(new InMemoryClusterStateProvider()) : null;
        var totp = new TotpVerificationService(cluster);
        var secret = totp.GenerateEnrollment("S-1-5-21-REPLAY", "replay@example.com").SecretBase32;
        var code = totp.GenerateTotpCode(secret);

        (await totp.VerifyAndConsumeTotpAsync("S-1-5-21-REPLAY", secret, code)).ShouldBeTrue();
        (await totp.VerifyAndConsumeTotpAsync("S-1-5-21-REPLAY", secret, code)).ShouldBeFalse();
        // Equivalent casing/whitespace SIDs map to the same replay marker.
        (await totp.VerifyAndConsumeTotpAsync(" s-1-5-21-replay ", secret, code)).ShouldBeFalse();

        if (cluster is not null)
        {
            var consumedCalls = cluster.IncrementCalls.FindAll(c => c.Key.StartsWith("totp:consumed:", StringComparison.Ordinal));
            // Attempt 1 marks the step in the cluster store; subsequent in-process replays are blocked before touching Redis.
            consumedCalls.Count.ShouldBe(1);
            // Replay marker TTL must cover at least 2 time steps (>= 60 seconds).
            consumedCalls.ShouldAllBe(c => c.Ttl >= TimeSpan.FromSeconds(60));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_does_not_reset_lockout_counter(bool useClusterStore)
    {
        var inner = new InMemoryClusterStateProvider();
        var cluster = useClusterStore ? new SpyClusterStateProvider(inner) : null;
        var totp = new TotpVerificationService(cluster, new TotpOptions { MaxFailedAttempts = 3, LockoutMinutes = 15 });
        const string userSid = "S-1-5-21-LOCK";
        var secret = totp.GenerateEnrollment(userSid, "lock@example.com").SecretBase32;

        var now = DateTimeOffset.UtcNow;
        var codeNow = totp.GenerateTotpCode(secret, now);
        var codeNextStep = totp.GenerateTotpCode(secret, now.AddSeconds(30));

        // Consume step once (resets any prior failures).
        (await totp.VerifyAndConsumeTotpAsync(userSid, secret, codeNow)).ShouldBeTrue();

        // One wrong guess -> failure count = 1.
        var wrong = codeNow == "000000" ? "111111" : "000000";
        (await totp.VerifyAndConsumeTotpAsync(userSid, secret, wrong)).ShouldBeFalse();

        // Replay the consumed code twice -> counts as failures 2 and 3, never resetting or reducing the counter.
        (await totp.VerifyAndConsumeTotpAsync(userSid, secret, codeNow)).ShouldBeFalse();
        (await totp.VerifyAndConsumeTotpAsync(userSid, secret, codeNow)).ShouldBeFalse();

        if (cluster is not null)
        {
            cluster.RemoveCalls.FindAll(k => k.StartsWith("totp:fail:", StringComparison.Ordinal)).Count.ShouldBe(1);
            (await inner.GetAsync<long>("totp:fail:s-1-5-21-lock")).ShouldBe(3L);
        }

        // Now at MaxFailedAttempts (3): even a fresh valid code from the next step is rejected, and replay does not unlock.
        (await totp.VerifyAndConsumeTotpAsync(userSid, secret, codeNextStep)).ShouldBeFalse();
        (await totp.VerifyAndConsumeTotpAsync(userSid, secret, codeNow)).ShouldBeFalse();

        if (cluster is not null)
        {
            (await inner.GetAsync<long>("totp:fail:s-1-5-21-lock")).ShouldBe(5L);
            cluster.RemoveCalls.FindAll(k => k.StartsWith("totp:fail:", StringComparison.Ordinal)).Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Used_step_tracked_across_nodes_store()
    {
        var sharedCluster = new SpyClusterStateProvider(new InMemoryClusterStateProvider());
        var nodeA = new TotpVerificationService(sharedCluster);
        var nodeB = new TotpVerificationService(sharedCluster);
        var secret = nodeA.GenerateEnrollment("S-1-5-21-CLUSTER", "cluster@example.com").SecretBase32;
        var code = nodeA.GenerateTotpCode(secret);

        (await nodeA.VerifyAndConsumeTotpAsync("S-1-5-21-CLUSTER", secret, code)).ShouldBeTrue();
        // A different node sharing the cluster store rejects the same step immediately.
        (await nodeB.VerifyAndConsumeTotpAsync("S-1-5-21-CLUSTER", secret, code)).ShouldBeFalse();

        // Fail closed when the shared cluster store is unavailable on consumption.
        var unavailableNode = new TotpVerificationService(new UnavailableIncrementClusterStateProvider());
        var freshCode = unavailableNode.GenerateTotpCode(secret, DateTimeOffset.UtcNow.AddSeconds(30));
        (await unavailableNode.VerifyAndConsumeTotpAsync("S-1-5-21-CLUSTER", secret, freshCode)).ShouldBeFalse();
    }

    [Fact]
    public async Task Missing_seed_denies_not_downgrades()
    {
        var cluster = new InMemoryClusterStateProvider();
        var totp = new TotpVerificationService(cluster);
        var secretStore = new ClusterTotpSecretStore(cluster);
        const string approverSid = "S-1-5-21-NO-SEED";

        // 1. Direct verification denies empty/null seeds.
        (await totp.VerifyAndConsumeTotpAsync(approverSid, string.Empty, "123456")).ShouldBeFalse();
        (await totp.VerifyAndConsumeTotpAsync(approverSid, null!, "123456")).ShouldBeFalse();

        // 2. HitL step-up approval denies when RequireTotp2Fa is enabled (default) and no seed is enrolled.
        var hitlOptions = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions
            {
                Enabled = true,
                ApprovalTimeoutSeconds = 5,
                RequireDifferentApprover = true,
                RequireTotp2Fa = true
            }
        });
        var hitl = new HitLStepUpApprovalService(
            hitlOptions,
            NullLogger<HitLStepUpApprovalService>.Instance,
            scopeFactory: null,
            clusterState: cluster,
            totpVerificationService: totp,
            totpSecretStore: secretStore);

        var ticketTask = hitl.RequestStepUpApprovalAsync(
            "query_table",
            "tenant-a",
            "S-1-5-21-REQUESTER",
            new TableIdentifier("sales", "dbo", "orders"));
        var pending = hitl.GetPendingTickets("tenant-a");
        pending.Count.ShouldBe(1);
        var ticket = pending[0];

        var approver = new HitLApproverContext(approverSid, [approverSid], "tenant-a");
        (await hitl.ApproveStepUpRequestAsync(ticket.ApprovalId, approver, totpCode: null)).IsApproved.ShouldBeFalse();
        (await hitl.ApproveStepUpRequestAsync(ticket.ApprovalId, approver, totpCode: "123456")).IsApproved.ShouldBeFalse();

        // After seed removal, approval must still deny and require re-enrollment (never degrade to single-factor).
        var tempSecret = totp.GenerateEnrollment(approverSid, "approver@example.com").SecretBase32;
        await secretStore.SetSecretAsync(approverSid, tempSecret);
        await secretStore.RemoveSecretAsync(approverSid);
        var codeForRemovedSeed = totp.GenerateTotpCode(tempSecret);
        (await hitl.ApproveStepUpRequestAsync(ticket.ApprovalId, approver, codeForRemovedSeed)).IsApproved.ShouldBeFalse();
        (await hitl.GetTicketAsync(ticket.ApprovalId))!.Status.ShouldBe(HitLApprovalStatus.Pending);

        // 3. Access plan confirmation denies when seed is missing.
        var planning = new AccessPlanningService(
            new InMemoryAuditWriter(),
            totpVerificationService: totp,
            totpSecretStore: secretStore);
        var plan = await planning.PlanAccessAsync(
            new AdminPlanAccessRequest(
                "sales.dbo.orders",
                [new PrincipalAccessGrant("S-1-5-21-GRANTEE", new Dictionary<string, string> { ["email"] = "mask" }, ValidUntil: DateTimeOffset.UtcNow.AddDays(5))],
                "Need access"),
            approverSid,
            "tenant-a");
        await Should.ThrowAsync<SecurityException>(() => planning.ConfirmPlanAsync(plan.PlanId, "123456", approverSid));

        // 4. Classification step-up approval denies when seed is missing.
        var proposalStore = new InMemoryClassificationProposalStore();
        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            new TableIdentifier("sales", "dbo", "orders"),
            approverSid,
            "PENDING_OWNER_REVIEW",
            [new ColumnClassificationProposal("ssn", "varchar", "PII", 4, null, "MASK", 0.9, false)],
            DateTimeOffset.UtcNow,
            ProposedBySid: "S-1-5-21-PROPOSER");
        await proposalStore.SaveProposalAsync(proposal);
        var workflow = new ClassificationWorkflowStateMachine(
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions { Classification = new ClassificationOptions() }),
            NullLogger<ClassificationWorkflowStateMachine>.Instance,
            proposalStore,
            totpService: totp,
            totpSecretStore: secretStore);
        var classResult = await workflow.TransitionAsync(
            proposal,
            new TableClassificationApprovalRequest(
                proposal.ProposalId,
                proposal.TableIdentifier,
                new Sid(approverSid),
                "DATA_OWNER",
                "APPROVE",
                [],
                StepUpAuthToken: "123456"));
        classResult.Success.ShouldBeFalse();
        classResult.ErrorCode.ShouldBe("INVALID_STEP_UP_AUTH_TOKEN");

        // 5. Missing seed cannot be bypassed during enrollment without completing BeginEnrollment + valid code.
        var enrollment = new TotpEnrollmentService(totp, secretStore, cluster);
        (await enrollment.HasActiveSecretAsync(approverSid)).ShouldBeFalse();
        (await enrollment.CompleteEnrollmentAsync(approverSid, "123456", null)).ShouldBe(TotpEnrollmentOutcome.NoPendingEnrollment);
        _ = ticketTask;
    }

    [Fact]
    public async Task Seed_has_no_implicit_ttl()
    {
        // 1. ClusterTotpSecretStore passes TimeSpan.MaxValue (no finite TTL) to IDistributedClusterStateProvider.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var backing = new InMemoryClusterStateProvider(time);
        var spy = new SpyClusterStateProvider(backing);
        var store = new ClusterTotpSecretStore(spy);

        await store.SetSecretAsync("S-1-5-21-DURABLE", "JBSWY3DPEHPK3PXP");

        spy.SetCalls.Count.ShouldBe(1);
        spy.SetCalls[0].Key.ShouldBe("totp:secret:s-1-5-21-durable");
        spy.SetCalls[0].Ttl.ShouldBe(TimeSpan.MaxValue);

        // Seed remains valid even after advancing time by 10 years.
        time.Advance(TimeSpan.FromDays(3650));
        (await store.GetSecretAsync("S-1-5-21-DURABLE")).ShouldBe("JBSWY3DPEHPK3PXP");

        // 2. RedisClusterStateProvider maps TimeSpan.MaxValue to persistent (default Expiration = no TTL).
        var expirations = new List<object?>();
        var db = MethodDispatchProxy<IDatabase>.Create((method, args) =>
        {
            if (method.Name == nameof(IDatabase.StringSetAsync))
            {
                expirations.Add(args is { Length: >= 3 } ? args[2] : null);
                return Task.FromResult(true);
            }

            return method.ReturnType == typeof(Task<bool>) ? Task.FromResult(true) : null;
        });
        var mux = MethodDispatchProxy<IConnectionMultiplexer>.Create((method, _) =>
            method.Name == nameof(IConnectionMultiplexer.GetDatabase) ? db : null);

        var redisProvider = new RedisClusterStateProvider(
            mux,
            Options.Create(new GatewayOptions()),
            NullLogger<RedisClusterStateProvider>.Instance);
        var redisStore = new ClusterTotpSecretStore(redisProvider);

        await redisStore.SetSecretAsync("S-1-5-21-REDIS", "JBSWY3DPEHPK3PXP");

        expirations.Count.ShouldBe(1);
        var exp = expirations[0].ShouldBeOfType<Expiration>();
        exp.Equals(default(Expiration)).ShouldBeTrue();
    }

    private sealed class SpyClusterStateProvider(IDistributedClusterStateProvider inner) : IDistributedClusterStateProvider
    {
        public List<(string Key, TimeSpan Ttl)> SetCalls { get; } = [];
        public List<(string Key, long Delta, TimeSpan Ttl)> IncrementCalls { get; } = [];
        public List<string> RemoveCalls { get; } = [];

        public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default) => inner.GetAsync<T>(key, ct);

        public ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
        {
            SetCalls.Add((key, ttl));
            return inner.SetAsync(key, value, ttl, ct);
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default)
        {
            RemoveCalls.Add(key);
            return inner.RemoveAsync(key, ct);
        }

        public ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default)
        {
            IncrementCalls.Add((key, delta, ttl));
            return inner.IncrementAsync(key, delta, ttl, ct);
        }

        public ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default) =>
            inner.TryConsumeBudgetAsync(key, cost, limit, ttl, ct);

        public ValueTask PublishEventAsync<T>(string channel, T payload, CancellationToken ct = default) =>
            inner.PublishEventAsync(channel, payload, ct);

        public IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default) =>
            inner.SubscribeAsync(channel, handler, ct);

        public ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default) =>
            inner.TryAcquireLockAsync(resourceKey, expiry, ct);

        public ValueTask<IDistributedLease?> TryAcquireLeaseAsync(string resourceKey, TimeSpan ttl, CancellationToken ct = default) =>
            inner.TryAcquireLeaseAsync(resourceKey, ttl, ct);

        public ValueTask<bool?> TryAcquireSlotAsync(string setKey, string member, int limit, TimeSpan ttl, CancellationToken ct = default) =>
            inner.TryAcquireSlotAsync(setKey, member, limit, ttl, ct);

        public ValueTask<bool> RenewSlotAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct = default) =>
            inner.RenewSlotAsync(setKey, member, ttl, ct);

        public ValueTask ReleaseSlotAsync(string setKey, string member, CancellationToken ct = default) =>
            inner.ReleaseSlotAsync(setKey, member, ct);
    }

    private sealed class UnavailableIncrementClusterStateProvider : IDistributedClusterStateProvider
    {
        public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default) => ValueTask.FromResult<T?>(default);
        public ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default) => ValueTask.FromResult<long?>(null);
        public ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default) => ValueTask.FromResult((BudgetConsumeOutcome.StoreUnavailable, 0L));
        public ValueTask PublishEventAsync<T>(string channel, T payload, CancellationToken ct = default) => ValueTask.CompletedTask;
        public IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default) => ValueTask.FromResult<IAsyncDisposable?>(null);
        public ValueTask<IDistributedLease?> TryAcquireLeaseAsync(string resourceKey, TimeSpan ttl, CancellationToken ct = default) => ValueTask.FromResult<IDistributedLease?>(null);
        public ValueTask<bool?> TryAcquireSlotAsync(string setKey, string member, int limit, TimeSpan ttl, CancellationToken ct = default) => ValueTask.FromResult<bool?>(null);
        public ValueTask<bool> RenewSlotAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask ReleaseSlotAsync(string setKey, string member, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

#pragma warning disable CA1852 // DispatchProxy subclasses cannot be sealed.
    private class MethodDispatchProxy<T> : DispatchProxy where T : class
#pragma warning restore CA1852
    {
        private Func<MethodInfo, object?[]?, object?> _handler = null!;

        public static T Create(Func<MethodInfo, object?[]?, object?> handler)
        {
            var proxy = Create<T, MethodDispatchProxy<T>>();
            ((MethodDispatchProxy<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handler(targetMethod!, args);
    }
}
