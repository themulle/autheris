namespace Autheris.Tests.Unit;

using System;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class DifferentialPrivacyTests
{
    [Fact]
    public async Task PerturbAsync_ShouldPerturbValueWithLaplaceNoiseAndDeductBudget()
    {
        // Arrange
        var engine = new DifferentialPrivacyEngine();
        var clientId = "analyst-alice";

        var request = new DifferentialPrivacyPerturbationRequest(
            ClientId: clientId,
            Value: 1000.0,
            Epsilon: 0.5,
            Sensitivity: 1.0,
            CohortCount: 20,
            MinimumCohortSize: 5
        );

        // Act
        var result = await engine.PerturbAsync(request);

        // Assert
        result.ClientId.ShouldBe(clientId);
        result.OriginalValue.ShouldBe(1000.0);
        result.PerturbedValue.ShouldNotBeNull();
        result.IsSuppressed.ShouldBeFalse();
        result.ConsumedEpsilon.ShouldBe(0.5);
        result.RemainingEpsilon.ShouldBe(9.5);
        result.Noise.ShouldNotBe(0.0);

        var budget = await engine.GetBudgetAsync(clientId);
        budget.ConsumedEpsilon.ShouldBe(0.5);
        budget.RemainingEpsilon.ShouldBe(9.5);
        budget.IsExhausted.ShouldBeFalse();
    }

    [Fact]
    public async Task PerturbAsync_ShouldSuppressSmallCohortsBelowKAnonymityThreshold()
    {
        // Arrange
        var engine = new DifferentialPrivacyEngine();
        var clientId = "researcher-bob";

        var request = new DifferentialPrivacyPerturbationRequest(
            ClientId: clientId,
            Value: 42.0,
            Epsilon: 1.0,
            Sensitivity: 1.0,
            CohortCount: 3, // Below k=5
            MinimumCohortSize: 5
        );

        // Act
        var result = await engine.PerturbAsync(request);

        // Assert
        result.IsSuppressed.ShouldBeTrue();
        result.PerturbedValue.ShouldBeNull();
        result.SuppressionReason.ShouldNotBeNull();
        result.SuppressionReason.ShouldContain("k-anonymity");
        result.SuppressionReason.ShouldContain("below");
        result.ConsumedEpsilon.ShouldBe(0.0); // Does not consume privacy budget when suppressed
    }

    [Fact]
    public async Task PerturbAsync_ShouldThrowWhenDailyEpsilonBudgetIsExhausted()
    {
        // Arrange
        var engine = new DifferentialPrivacyEngine();
        var clientId = "bot-carol";

        // Total default budget is 10.0 epsilon. Consume 6.0 then 4.0 = 10.0
        await engine.PerturbAsync(new DifferentialPrivacyPerturbationRequest(clientId, 100, Epsilon: 6.0, CohortCount: 10));
        await engine.PerturbAsync(new DifferentialPrivacyPerturbationRequest(clientId, 100, Epsilon: 4.0, CohortCount: 10));

        var budget = await engine.GetBudgetAsync(clientId);
        budget.ConsumedEpsilon.ShouldBe(10.0);
        budget.RemainingEpsilon.ShouldBe(0.0);
        budget.IsExhausted.ShouldBeTrue();

        // Attempt to consume 0.5 more
        var ex = await Should.ThrowAsync<PrivacyBudgetExhaustedException>(() =>
            engine.PerturbAsync(new DifferentialPrivacyPerturbationRequest(clientId, 100, Epsilon: 0.5, CohortCount: 10)).AsTask());

        ex.ClientId.ShouldBe(clientId);
        ex.ConsumedEpsilon.ShouldBe(10.5);
        ex.TotalBudget.ShouldBe(10.0);

        // Reset budget manually
        await engine.ResetBudgetAsync(clientId);
        var budgetAfterReset = await engine.GetBudgetAsync(clientId);
        budgetAfterReset.ConsumedEpsilon.ShouldBe(0.0);
        budgetAfterReset.RemainingEpsilon.ShouldBe(10.0);
        budgetAfterReset.IsExhausted.ShouldBeFalse();

        // Now next call succeeds
        var afterResetResult = await engine.PerturbAsync(new DifferentialPrivacyPerturbationRequest(clientId, 100, Epsilon: 1.0, CohortCount: 10));
        afterResetResult.PerturbedValue.ShouldNotBeNull();
        afterResetResult.ConsumedEpsilon.ShouldBe(1.0);
    }
}
