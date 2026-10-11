namespace Autheris.Tests.SqlCompilerGate.Parity;

using Autheris.Domain.Model;
using MemoryPack;
using Shouldly;

/// <summary>
/// <c>ConsentCacheService</c> and <c>CachedConsentEnvelope</c> (§7.3): the L2 entry stores the row filter text only. The text of either producer must
/// survive the binary round trip byte for byte; the values of a typed text are not stored (the typed source is the recipe), which is why a cached
/// text without a recipe is treated as a miss by <c>TableAccessPolicy</c> instead of being bound.
/// </summary>
public sealed class ConsentCacheParityTests
{
    [Fact]
    public void Envelope_RoundTrip_PreservesTheTextOfBothProducers_ForEveryCase()
    {
        var context = new ParityContext(Model.GateDialect.PostgreSql, "public");
        int roundTripped = 0;
        foreach (var parityCase in ParityCases.All)
        {
            var pair = ParityRunner.TryProduce(parityCase, context, out _);
            if (pair is null) continue;
            foreach (var text in new[] { pair.Legacy, pair.Ir })
            {
                var decision = ParityData.Decision(context, text);
                var envelope = CachedConsentEnvelope.FromDecision(decision, epoch: 7);

                var restored = MemoryPackSerializer.Deserialize<CachedConsentEnvelope>(MemoryPackSerializer.Serialize(envelope))!.ToDecision();

                restored.CombinedRowFilterSql.ShouldBe(string.IsNullOrEmpty(text.Sql) ? null : text.Sql, parityCase.Id);
                // the envelope stores no values: a typed text read back from L2 has no bound parameters
                restored.RowFilterParameters.ShouldBeNull(parityCase.Id);
                roundTripped++;
            }
        }

        roundTripped.ShouldBeGreaterThan(80);
    }
}
