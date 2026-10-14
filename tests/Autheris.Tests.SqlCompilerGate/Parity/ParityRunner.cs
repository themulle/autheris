namespace Autheris.Tests.SqlCompilerGate.Parity;

using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;

/// <summary>The classified result of one consumer, one dialect and one case.</summary>
public sealed record ParityResult(
    string Consumer,
    GateDialect Dialect,
    ParityCase Case,
    ProducedPair? Produced,
    EngineOutcome Legacy,
    EngineOutcome Ir,
    Classification Classification,
    bool IrExceedsReference,
    bool IrMissesReference,
    bool LegacyDeviatesFromReference)
{
    /// <summary>The gate (G10): the IR result is never looser than legacy (direction judged as in G4) and never admits a row outside the reference.</summary>
    public bool IsLooser => Classification.Class.IsLooser() || IrExceedsReference;
}

public static class ParityRunner
{
    private static readonly PolicyOracle Oracle = new(PolicyScenario.Tenant);

    public static ISet<long> ReferenceIds(ParityCase parityCase) =>
        GateModel.Rows.Where(r => r.Table == "orders" && r.Tenant == GateModel.TenantA && parityCase.Reference!(r)).Select(r => r.Id).ToHashSet();

    public static ProducedPair? TryProduce(ParityCase parityCase, ParityContext context, out string? failure)
    {
        try
        {
            failure = null;
            return parityCase.Produce(context);
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name;
            return null;
        }
    }

    public static async Task<ParityResult> RunAsync(IParityConsumer consumer, ParityEnv env, ParityCase parityCase)
    {
        var produced = TryProduce(parityCase, env.Context, out var failure);
        if (produced is null)
        {
            var rejected = new EngineOutcome.Rejected(RejectionStage.Compile, failure ?? "ProducerFailed", true);
            return new ParityResult(consumer.Name, env.Context.Dialect, parityCase, null, rejected, rejected,
                Classifier.Classify(rejected, rejected, Oracle), false, false, false);
        }

        return await RunAsync(consumer, env, parityCase, produced);
    }

    /// <summary>Runs a pair that the caller produced (also used to feed a deliberately broken producer to the same classification).</summary>
    public static async Task<ParityResult> RunAsync(IParityConsumer consumer, ParityEnv env, ParityCase parityCase, ProducedPair produced)
    {
        var legacy = await consumer.RunAsync(env, produced.Legacy);
        var ir = await consumer.RunAsync(env, produced.Ir);
        var classification = Classifier.Classify(legacy, ir, Oracle);

        bool exceeds = false, misses = false, legacyDeviates = false;
        if (parityCase.Reference is not null && consumer is not ConnectorPushdownValidationConsumer)
        {
            var reference = ReferenceIds(parityCase);
            if (IdsOf(ir) is { } irIds)
            {
                exceeds = irIds.Any(id => !reference.Contains(id));
                misses = reference.Any(id => !irIds.Contains(id));
            }

            if (IdsOf(legacy) is { } legacyIds) legacyDeviates = !legacyIds.SetEquals(reference);
        }

        return new ParityResult(consumer.Name, env.Context.Dialect, parityCase, produced, legacy, ir, classification, exceeds, misses, legacyDeviates);
    }

    public static HashSet<long>? IdsOf(EngineOutcome outcome) =>
        outcome is EngineOutcome.Rows rows ? [.. rows.Data.Select(r => Convert.ToInt64(r[0], System.Globalization.CultureInfo.InvariantCulture))] : null;

    public static async Task<IReadOnlyList<ParityResult>> RunAllAsync(ParityEnv env, IEnumerable<IParityConsumer> consumers)
    {
        var results = new List<ParityResult>();
        foreach (var consumer in consumers)
        {
            foreach (var parityCase in ParityCases.All) results.Add(await RunAsync(consumer, env, parityCase));
        }

        return results;
    }

    /// <summary>One line per consumer and class, for the test output and the report.</summary>
    public static IEnumerable<string> Table(IEnumerable<ParityResult> results) =>
        results.GroupBy(r => r.Consumer).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            $"{g.Key} [{g.First().Dialect}]: " + string.Join(", ", g.GroupBy(r => r.Classification.Class.ToWireName()).OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => $"{c.Key}={c.Count()}")) +
            $", ir-exceeds-reference={g.Count(r => r.IrExceedsReference)}, ir-misses-reference={g.Count(r => r.IrMissesReference)}, legacy-deviates={g.Count(r => r.LegacyDeviatesFromReference)}");
}
