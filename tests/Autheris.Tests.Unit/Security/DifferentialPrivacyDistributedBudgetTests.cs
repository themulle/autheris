namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DifferentialPrivacyDistributedBudgetTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public TestTimeProvider(DateTimeOffset initial) => _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now = _now.Add(span);
    }

    [Fact]
    public async Task ParallelConsume_AcrossTwoEngines_NeverExceedsLimit()
    {
        // Arrange: 2 engines sharing a single InMemoryClusterStateProvider
        var sharedStore = new InMemoryClusterStateProvider();
        var engine1 = new DifferentialPrivacyEngine(sharedStore);
        var engine2 = new DifferentialPrivacyEngine(sharedStore);

        const string clientId = "distributed-client-1";
        const int totalRequests = 1000;
        const double costPerRequest = 0.1; // 1000 * 0.1 = 100.0, limit is 10.0 -> exactly 100 should succeed

        var successCount = 0;
        var exhaustedCount = 0;

        // Act: 1000 concurrent requests across engine1 and engine2
        await Parallel.ForEachAsync(Enumerable.Range(0, totalRequests), async (i, ct) =>
        {
            var engine = (i % 2 == 0) ? engine1 : engine2;
            try
            {
                var req = new DifferentialPrivacyPerturbationRequest(
                    ClientId: clientId,
                    Value: 100.0 + i,
                    Epsilon: costPerRequest,
                    Sensitivity: 1.0,
                    CohortCount: 10,
                    MinimumCohortSize: 5);

                var res = await engine.PerturbAsync(req, ct);
                if (res.PerturbedValue.HasValue && !res.IsSuppressed)
                {
                    Interlocked.Increment(ref successCount);
                }
            }
            catch (PrivacyBudgetExhaustedException)
            {
                Interlocked.Increment(ref exhaustedCount);
            }
        });

        // Assert: Exactly 100 succeed, 900 fail with PrivacyBudgetExhaustedException
        successCount.ShouldBe(100);
        exhaustedCount.ShouldBe(900);

        var finalBudget = await engine1.GetBudgetAsync(clientId);
        finalBudget.ConsumedEpsilon.ShouldBe(10.0);
        finalBudget.RemainingEpsilon.ShouldBe(0.0);
        finalBudget.IsExhausted.ShouldBeTrue();
    }

    [Fact]
    public async Task StoreUnavailable_FailsClosed_AndAudits()
    {
        // Arrange
        var mockStore = Substitute.For<IDistributedClusterStateProvider>();
        mockStore.TryConsumeBudgetAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(ValueTask.FromResult((BudgetConsumeOutcome.StoreUnavailable, 0L)));

        var mockAudit = Substitute.For<IAuditLogRepository>();
        var engine = new DifferentialPrivacyEngine(mockStore, mockAudit);

        var req = new DifferentialPrivacyPerturbationRequest(
            ClientId: "client-failing-store",
            Value: 42.0,
            Epsilon: 0.5,
            Sensitivity: 1.0,
            CohortCount: 10);

        // Act & Assert: Must fail-closed (throw InvalidOperationException)
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            engine.PerturbAsync(req).AsTask());

        ex.Message.ShouldContain("unavailable");

        // Verify audit log record
        await mockAudit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "DP_BUDGET_STORE_UNAVAILABLE" &&
                e.Decision == "DENY" &&
                e.ActorSid.Value == "client-failing-store"),
            Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public async Task DailyKey_RollsOverAtUtcMidnight()
    {
        // Arrange: Start at 23:55 UTC
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 9, 23, 55, 0, TimeSpan.Zero));
        var store = new InMemoryClusterStateProvider();
        var engine = new DifferentialPrivacyEngine(store, timeProvider: time);
        const string clientId = "client-rollover";

        // Consume all 10.0 epsilon on 2026-10-09
        await engine.PerturbAsync(new DifferentialPrivacyPerturbationRequest(clientId, 100, Epsilon: 10.0, CohortCount: 10));

        var budgetDay1 = await engine.GetBudgetAsync(clientId);
        budgetDay1.ConsumedEpsilon.ShouldBe(10.0);
        budgetDay1.RemainingEpsilon.ShouldBe(0.0);
        budgetDay1.IsExhausted.ShouldBeTrue();

        // Further request on same day fails
        await Should.ThrowAsync<PrivacyBudgetExhaustedException>(() =>
            engine.PerturbAsync(new DifferentialPrivacyPerturbationRequest(clientId, 100, Epsilon: 0.5, CohortCount: 10)).AsTask());

        // Advance 10 minutes to next day 00:05 UTC (2026-10-10)
        time.Advance(TimeSpan.FromMinutes(10));

        // Budget should be fresh 10.0 on new day
        var budgetDay2 = await engine.GetBudgetAsync(clientId);
        budgetDay2.ConsumedEpsilon.ShouldBe(0.0);
        budgetDay2.RemainingEpsilon.ShouldBe(10.0);
        budgetDay2.IsExhausted.ShouldBeFalse();

        // Request now succeeds on new day
        var res = await engine.PerturbAsync(new DifferentialPrivacyPerturbationRequest(clientId, 100, Epsilon: 2.5, CohortCount: 10));
        res.PerturbedValue.ShouldNotBeNull();
        res.ConsumedEpsilon.ShouldBe(2.5);
    }

    [Fact]
    public async Task EpsilonRounding_UsesCeilingMicroUnits()
    {
        // Arrange: Epsilon with sub-micro precision, e.g. 0.0000001 (0.1 micro-units)
        // With Math.Ceiling, it must be rounded UP to 1 micro-unit = 0.000001
        var store = new InMemoryClusterStateProvider();
        var engine = new DifferentialPrivacyEngine(store);
        const string clientId = "client-rounding";

        var req = new DifferentialPrivacyPerturbationRequest(
            ClientId: clientId,
            Value: 10.0,
            Epsilon: 0.0000001,
            Sensitivity: 1.0,
            CohortCount: 10);

        await engine.PerturbAsync(req);

        var budget = await engine.GetBudgetAsync(clientId);
        // Consumed epsilon in budget should reflect the ceiling micro-unit: 1 / 1_000_000 = 0.000001
        budget.ConsumedEpsilon.ShouldBe(0.000001);
    }
}
