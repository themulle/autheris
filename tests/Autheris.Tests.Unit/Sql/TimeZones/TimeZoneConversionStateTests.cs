namespace Autheris.Tests.Unit.Sql.TimeZones;

using Autheris.Application.Sql.TimeZones;
using Autheris.Tests.Unit.Databricks;
using Microsoft.Extensions.Logging;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>REQ-SQL-130 (A-7, BC-130-7): availability of the conversion per dialect, evaluated once, fail closed, one Warning without driver text.</summary>
public sealed class TimeZoneConversionStateTests
{
    private static readonly TimeZoneSelfTestResult Pass = new(true, null, true);

    private static readonly TargetSqlDialect[] AllDialects = Enum.GetValues<TargetSqlDialect>();

    private static (TimeZoneConversionState State, CapturingLogger<TimeZoneConversionState> Log, int[] Probes) Create(TimeZoneSelfTestResult selfTest, Func<DuckDbProbeResult> probe)
    {
        var log = new CapturingLogger<TimeZoneConversionState>();
        var probes = new int[1];
        var state = new TimeZoneConversionState(log, () => selfTest, () => { probes[0]++; return probe(); });
        return (state, log, probes);
    }

    [Fact]
    public void T22_AnUnevaluatedStateAnswersFalseForEveryDialect()
    {
        var (state, _, probes) = Create(Pass, () => DuckDbProbeResult.Ok);
        foreach (var dialect in AllDialects) state.IsAvailable(dialect).ShouldBeFalse();
        probes[0].ShouldBe(0);
    }

    [Fact]
    public void T22_WhenEverythingPassesOnlyTheDialectsWithARuleAreAvailable()
    {
        var (state, log, _) = Create(Pass, () => DuckDbProbeResult.Ok);
        state.Evaluate();
        foreach (var dialect in AllDialects)
        {
            state.IsAvailable(dialect).ShouldBe(dialect is TargetSqlDialect.SqlServer or TargetSqlDialect.PostgreSql or TargetSqlDialect.DuckDb);
        }

        log.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void T22_ASelfTestFailureMakesEveryDialectUnavailableAndLogsOneWarningWithTheCheckIdOnly()
    {
        var (state, log, probes) = Create(new TimeZoneSelfTestResult(false, "hash", true), () => DuckDbProbeResult.Ok);
        state.Evaluate();
        foreach (var dialect in AllDialects) state.IsAvailable(dialect).ShouldBeFalse();

        probes[0].ShouldBe(0);   // no point probing DuckDB when the registry itself is not trustworthy
        var warning = log.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Text.ShouldContain("hash");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T22_ADuckDbProbeFailureMakesOnlyDuckDbUnavailableAndTheWarningCarriesNoDriverText()
    {
        var (state, log, _) = Create(Pass, () => throw new InvalidOperationException("SECRET-DRIVER-TEXT /home/user/.duckdb path"));
        state.Evaluate();

        state.IsAvailable(TargetSqlDialect.DuckDb).ShouldBeFalse();
        state.IsAvailable(TargetSqlDialect.SqlServer).ShouldBeTrue();
        state.IsAvailable(TargetSqlDialect.PostgreSql).ShouldBeTrue();

        var warning = log.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Text.ShouldNotContain("SECRET-DRIVER-TEXT");
        warning.Text.ShouldNotContain("duckdb path");
    }

    [Theory]
    [InlineData(DuckDbProbeResult.ConversionMismatch)]
    [InlineData(DuckDbProbeResult.IcuNotStatic)]
    [InlineData(DuckDbProbeResult.SandboxChanged)]
    [Trait("Category", "Security")]
    public void ST_130_15_ASimulatedFailedProbeMakesDuckDbUnavailable(DuckDbProbeResult result)
    {
        var (state, log, _) = Create(Pass, () => result);
        state.Evaluate();
        state.IsAvailable(TargetSqlDialect.DuckDb).ShouldBeFalse();
        state.IsAvailable(TargetSqlDialect.PostgreSql).ShouldBeTrue();
        log.Entries.ShouldHaveSingleItem().Text.ShouldContain(result.ToString());
    }

    [Fact]
    public void T22_TheEvaluationIsCachedForTheProcessLifetime()
    {
        int calls = 0;
        var log = new CapturingLogger<TimeZoneConversionState>();
        var state = new TimeZoneConversionState(log, () => Pass, () => { calls++; return calls == 1 ? DuckDbProbeResult.ConversionMismatch : DuckDbProbeResult.Ok; });
        state.Evaluate();
        state.Evaluate();
        state.IsAvailable(TargetSqlDialect.DuckDb).ShouldBeFalse();   // a failure keeps DuckDB unavailable until restart
        calls.ShouldBe(1);
        log.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public void T22_ASelfTestThatThrowsIsAFailure()
    {
        var log = new CapturingLogger<TimeZoneConversionState>();
        var state = new TimeZoneConversionState(log, () => throw new InvalidOperationException("boom"), () => DuckDbProbeResult.Ok);
        state.Evaluate();
        foreach (var dialect in AllDialects) state.IsAvailable(dialect).ShouldBeFalse();
        log.Entries.ShouldHaveSingleItem().Text.ShouldNotContain("boom");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_130_15_TheRealRegistryAndTheRealDuckDbProbePassUnderTheProductionSandbox()
    {
        var log = new CapturingLogger<TimeZoneConversionState>();
        var state = new TimeZoneConversionState(log);
        state.Evaluate();
        state.IsAvailable(TargetSqlDialect.SqlServer).ShouldBeTrue();
        state.IsAvailable(TargetSqlDialect.PostgreSql).ShouldBeTrue();
        state.IsAvailable(TargetSqlDialect.DuckDb).ShouldBeTrue();
        log.Entries.ShouldBeEmpty();
        DuckDbIcuProbe.Run().ShouldBe(DuckDbProbeResult.Ok);
    }
}
