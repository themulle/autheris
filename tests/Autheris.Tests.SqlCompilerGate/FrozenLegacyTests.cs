namespace Autheris.Tests.SqlCompilerGate;

using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;

/// <summary>X1.4: the frozen legacy results (the legacy engine is deleted) are complete, readable and usable by the classifier.</summary>
public sealed class FrozenLegacyTests
{
    [Fact]
    public void Codec_RoundTripsEveryOutcomeShapeAndCellType()
    {
        object?[] cells = ["s", new byte[] { 1, 2 }, true, (sbyte)1, (byte)2, (short)3, 4, 5L, 6.5m, 7.25d, 8.5f, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)), null];
        var results = new[]
        {
            FrozenLegacy.WriteOutcome("aaaaaaaaaaaa", PolicyScenario.Tenant, new EngineOutcome.Rows([cells])),
            FrozenLegacy.WriteOutcome("bbbbbbbbbbbb", PolicyScenario.Tenant, new EngineOutcome.Written([new RowChange('-', "orders", cells), new RowChange('+', "orders", cells)])),
            FrozenLegacy.WriteOutcome("cccccccccccc", PolicyScenario.TenantConsentMask, new EngineOutcome.Rejected(RejectionStage.Compile, "SecurityException", true))
        };

        var frozen = FrozenLegacy.Parse(FrozenLegacy.Serialize("Sqlite", "abc", results));

        frozen.Count.ShouldBe(3);
        frozen.TryGet("aaaaaaaaaaaa", PolicyScenario.Tenant, out var rows).ShouldBeTrue();
        var read = ((EngineOutcome.Rows)rows).Data.Single();
        read.Length.ShouldBe(cells.Length);
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] is byte[] expected) ((byte[])read[i]!).ShouldBe(expected);
            else read[i].ShouldBe(cells[i]);
            read[i]?.GetType().ShouldBe(cells[i]!.GetType());
        }

        frozen.TryGet("bbbbbbbbbbbb", PolicyScenario.Tenant, out var written).ShouldBeTrue();
        ((EngineOutcome.Written)written).Changes.Select(c => c.Sign).ShouldBe(['-', '+']);
        frozen.TryGet("cccccccccccc", PolicyScenario.TenantConsentMask, out var rejected).ShouldBeTrue();
        rejected.ShouldBe(new EngineOutcome.Rejected(RejectionStage.Compile, "SecurityException", true));
        frozen.TryGet("cccccccccccc", PolicyScenario.Tenant, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(GateDialect.Sqlite)]
    [InlineData(GateDialect.PostgreSql)]
    [InlineData(GateDialect.SqlServer)]
    [InlineData(GateDialect.Oracle)]
    public void GoldenResults_CoverTheWholeCorpus_InEveryScenario(GateDialect dialect)
    {
        var frozen = FrozenLegacy.Load(dialect);

        var missing = (from statement in CorpusLoader.LoadAll()
                       from scenario in Enum.GetValues<PolicyScenario>()
                       where !frozen.TryGet(statement.Id, scenario, out _)
                       select $"{statement.Id}/{scenario}").ToList();

        missing.ShouldBeEmpty($"{missing.Count} corpus statements without a frozen legacy result on {dialect}");
        frozen.Dialect.ShouldBe(dialect.ToString());
        frozen.FrozenFrom.ShouldBe("c1a2af2d", "the results were recorded on the pre-cutover head");
    }

    [Fact]
    public void RequireFrozenLegacy_FailsWhenAStatementHasNoFrozenResult()
    {
        Should.NotThrow(() => DifferentialRules.RequireFrozenLegacy("Sqlite", []));
        Should.Throw<Exception>(() => DifferentialRules.RequireFrozenLegacy("Sqlite", ["abc/Tenant"])).Message.ShouldContain("no frozen legacy result");
    }

    [Fact]
    public void FrozenRows_AreClassifiedDirectionally_AgainstALiveOutcome()
    {
        var oracle = new PolicyOracle(PolicyScenario.Tenant);
        var legacy = new EngineOutcome.Rows([["a"]]);

        Classifier.Classify(legacy, new EngineOutcome.Rows([["a"]]), oracle).Class.ShouldBe(DifferenceClass.Identical);
        Classifier.Classify(legacy, new EngineOutcome.Rejected(RejectionStage.Compile, "SecurityException", true), oracle).Class.ShouldBe(DifferenceClass.AstStricter);
    }
}
