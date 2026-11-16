namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using Autheris.Application.Usage;
using Shouldly;
using Xunit;

public sealed class UsageTariffCalculatorTests
{
    private static readonly DateOnly Day1 = new(2026, 11, 10);
    private static readonly DateOnly Day2 = new(2026, 11, 15);
    private static readonly DateTimeOffset Created = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Per_query_fee_only()
    {
        var tariffs = new[] { Tariff("t1", "pg-main", 1, Day1, null, basePerQuery: 0.25m) };
        var row = Row("t1", Day1, "pg-main", hasGrant: true, queries: 4, rows: 1000, bytes: 10_000_000, durationMs: 5000);

        var result = UsageTariffCalculator.CalculateRow(row, tariffs);

        result.TariffMissing.ShouldBeFalse();
        result.CostUnits.ShouldBe(1.000000m);
        result.UnitLabel.ShouldBe("credits");
        result.AppliedVersionId.ShouldBe("t1/pg-main/v1");
    }

    [Fact]
    public void Per_row_and_per_mib()
    {
        var tariffs = new[] { Tariff("t1", "pg-main", 1, Day1, null, perRow: 0.001m, perMib: 2m) };
        var row = Row("t1", Day1, "pg-main", hasGrant: true, queries: 1, rows: 500, bytes: 524_288, durationMs: 100);

        var result = UsageTariffCalculator.CalculateRow(row, tariffs);

        // 500 * 0.001 + 2 * (524288 / 1048576) = 0.5 + 1.0 = 1.5
        result.CostUnits.ShouldBe(1.5m);
    }

    [Fact]
    public void Per_compute_second_uses_source_compute_when_all_queries_measured()
    {
        var tariffs = new[] { Tariff("t1", "dbx", 1, Day1, null, perComputeSecond: 0.5m, computeBasis: 0) };
        var row = Row("t1", Day1, "dbx", hasGrant: true, queries: 2, rows: 10, bytes: 1024, durationMs: 10_000, sourceComputeMs: 4_000, sourceComputeMeasured: 2);

        var result = UsageTariffCalculator.CalculateRow(row, tariffs);

        // sourceComputeMeasured == queries -> 4000ms = 4s * 0.5 = 2.0 (ignores 10s gateway duration)
        result.CostUnits.ShouldBe(2.0m);
    }

    [Fact]
    public void Per_compute_second_falls_back_to_duration()
    {
        var tariffs = new[] { Tariff("t1", "dbx", 1, Day1, null, perComputeSecond: 0.5m, computeBasis: 0) };
        var partial = Row("t1", Day1, "dbx", hasGrant: true, queries: 2, rows: 10, bytes: 1024, durationMs: 10_000, sourceComputeMs: 4_000, sourceComputeMeasured: 1);

        var partialResult = UsageTariffCalculator.CalculateRow(partial, tariffs);
        partialResult.CostUnits.ShouldBe(5.0m);

        var basisOneTariffs = new[] { Tariff("t1", "dbx", 1, Day1, null, perComputeSecond: 0.5m, computeBasis: 1) };
        var measured = Row("t1", Day1, "dbx", hasGrant: true, queries: 2, rows: 10, bytes: 1024, durationMs: 10_000, sourceComputeMs: 4_000, sourceComputeMeasured: 2);
        UsageTariffCalculator.CalculateRow(measured, basisOneTariffs).CostUnits.ShouldBe(5.0m);
    }

    [Fact]
    public void Pass_through_source_cost_with_factor()
    {
        var tariffs = new[]
        {
            Tariff("t1", "dbx", 1, Day1, null, sourceCostFactor: 1.5m, sourceCostUnit: "DBU", unitLabel: "USD")
        };
        var row = Row("t1", Day1, "dbx", hasGrant: true, queries: 1, rows: 1, bytes: 100, durationMs: 100, sourceCostMicros: 2_500_000);

        var result = UsageTariffCalculator.CalculateRow(row, tariffs);

        // 1.5 * (2,500,000 / 1,000,000) = 3.75
        result.CostUnits.ShouldBe(3.75m);
        result.UnitLabel.ShouldBe("USD");
    }

    [Fact]
    public void Combination_is_sum_of_parts()
    {
        var tariffs = new[]
        {
            Tariff("t1", "dbx", 1, Day1, null,
                basePerQuery: 0.1m,
                perRow: 0.002m,
                perMib: 0.5m,
                perComputeSecond: 0.25m,
                perAnalysisCost: 0.01m,
                sourceCostFactor: 2m,
                sourceCostUnit: "DBU")
        };
        var row = Row("t1", Day1, "dbx", hasGrant: true,
            queries: 2, rows: 100, bytes: 2_097_152, durationMs: 8_000, analysisCost: 15,
            sourceComputeMs: 4_000, sourceComputeMeasured: 2, sourceCostMicros: 500_000);

        var result = UsageTariffCalculator.CalculateRow(row, tariffs);

        // 0.2 + 0.2 + 1.0 + (0.25*4 = 1.0) + 0.15 + (2 * 0.5 = 1.0) = 3.55
        result.CostUnits.ShouldBe(3.55m);
    }

    [Fact]
    public void Aggregate_pricing_equals_per_query_pricing()
    {
        var tariffs = new[]
        {
            Tariff("t1", "pg-main", 1, Day1, null,
                basePerQuery: 0.015m,
                perRow: 0.0003m,
                perMib: 0.12m,
                perComputeSecond: 0.08m,
                perAnalysisCost: 0.005m,
                sourceCostFactor: 1.1m,
                sourceCostUnit: "CU",
                computeBasis: 0)
        };

        var rng = new Random(42);
        var facts = new List<UsageTariffRowInput>(1000);
        long totalQueries = 0, totalRows = 0, totalBytes = 0, totalDuration = 0, totalAnalysis = 0, totalSrcMs = 0, totalSrcCost = 0;

        for (int i = 0; i < 1000; i++)
        {
            long rows = rng.Next(0, 5000);
            long bytes = rng.Next(0, 2_000_000);
            long dur = rng.Next(1, 3000);
            long analysis = rng.Next(0, 50);
            long srcMs = rng.Next(1, 3000);
            long srcCost = rng.Next(0, 200_000);

            totalQueries += 1;
            totalRows += rows;
            totalBytes += bytes;
            totalDuration += dur;
            totalAnalysis += analysis;
            totalSrcMs += srcMs;
            totalSrcCost += srcCost;

            facts.Add(new UsageTariffRowInput(
                "t1", Day1, "pg-main", HasGrant: true,
                Queries: 1, RowsReturned: rows, Bytes: bytes, DurationMs: dur, AnalysisCost: analysis,
                SourceComputeMs: srcMs, SourceComputeMeasured: 1, SourceCostMicros: srcCost));
        }

        decimal sumOfPerQueryRaw = 0m;
        foreach (var f in facts)
        {
            sumOfPerQueryRaw += UsageTariffCalculator.CalculateRow(f, tariffs).RawCostUnits;
        }

        var aggregateInput = new UsageTariffRowInput(
            "t1", Day1, "pg-main", HasGrant: true,
            totalQueries, totalRows, totalBytes, totalDuration, totalAnalysis,
            totalSrcMs, totalQueries, totalSrcCost);
        decimal aggregateRounded = UsageTariffCalculator.CalculateRow(aggregateInput, tariffs).CostUnits;

        UsageTariffCalculator.RoundCost(sumOfPerQueryRaw).ShouldBe(aggregateRounded);
    }

    [Fact]
    public void Tariff_lookup_order_tenant_source_then_wildcards()
    {
        var globalFallback = Tariff("*", "*", 1, Day1, null, basePerQuery: 1m);
        var globalSource = Tariff("*", "pg-main", 1, Day1, null, basePerQuery: 2m);
        var tenantWildcard = Tariff("t1", "*", 1, Day1, null, basePerQuery: 3m);
        var tenantSource = Tariff("t1", "pg-main", 1, Day1, null, basePerQuery: 4m);

        var row = Row("t1", Day1, "pg-main", hasGrant: true, queries: 1);

        UsageTariffCalculator.CalculateRow(row, [globalFallback, globalSource, tenantWildcard, tenantSource]).CostUnits.ShouldBe(4m);
        UsageTariffCalculator.CalculateRow(row, [globalFallback, globalSource, tenantWildcard]).CostUnits.ShouldBe(3m);
        UsageTariffCalculator.CalculateRow(row, [globalFallback, globalSource]).CostUnits.ShouldBe(2m);
        UsageTariffCalculator.CalculateRow(row, [globalFallback]).CostUnits.ShouldBe(1m);
    }

    [Fact]
    public void Day_uses_version_valid_on_that_day()
    {
        var tariffs = new[]
        {
            Tariff("t1", "pg-main", 1, Day1, Day2, basePerQuery: 1.5m),
            Tariff("t1", "pg-main", 2, Day2, null, basePerQuery: 3.0m)
        };

        // Day1 <= d < Day2 -> v1; Day2 -> v2 (half-open ValidUntil)
        UsageTariffCalculator.CalculateRow(Row("t1", Day1, "pg-main", true, 1), tariffs).CostUnits.ShouldBe(1.5m);
        UsageTariffCalculator.CalculateRow(Row("t1", Day2.AddDays(-1), "pg-main", true, 1), tariffs).CostUnits.ShouldBe(1.5m);
        UsageTariffCalculator.CalculateRow(Row("t1", Day2, "pg-main", true, 1), tariffs).CostUnits.ShouldBe(3.0m);
    }

    [Fact]
    public void No_tariff_yields_zero_and_flag()
    {
        var tariffs = new[] { Tariff("t1", "pg-main", 1, Day2, null, basePerQuery: 2m) };
        var result = UsageTariffCalculator.CalculateRow(Row("t1", Day1, "pg-main", hasGrant: true, queries: 3), tariffs);

        result.CostUnits.ShouldBe(0m);
        result.TariffMissing.ShouldBeTrue();
        result.UnitLabel.ShouldBeNull();
    }

    [Fact]
    public void Denies_and_requests_without_grant_cost_zero()
    {
        var tariffs = new[] { Tariff("t1", "pg-main", 1, Day1, null, basePerQuery: 5m, perRow: 1m) };
        var denyRow = Row("t1", Day1, "pg-main", hasGrant: false, queries: 1, rows: 100, bytes: 10_000, durationMs: 5000);

        var result = UsageTariffCalculator.CalculateRow(denyRow, tariffs);

        result.CostUnits.ShouldBe(0m);
        result.TariffMissing.ShouldBeFalse();
        result.AppliedVersionId.ShouldBeNull();
    }

    [Fact]
    public void Mixed_unit_labels_not_summed()
    {
        var tariffs = new[]
        {
            Tariff("t1", "pg-main", 1, Day1, null, basePerQuery: 1.25m, unitLabel: "credits"),
            Tariff("t1", "dbx", 1, Day1, null, basePerQuery: 2.50m, unitLabel: "USD")
        };

        var summary = UsageTariffCalculator.CalculateSummary(
        [
            Row("t1", Day1, "pg-main", true, queries: 2),
            Row("t1", Day1, "dbx", true, queries: 3)
        ], tariffs);

        summary.TariffMissing.ShouldBeFalse();
        summary.Buckets.Count.ShouldBe(2);
        summary.Buckets.Single(b => b.UnitLabel == "credits").CostUnits.ShouldBe(2.50m);
        summary.Buckets.Single(b => b.UnitLabel == "USD").CostUnits.ShouldBe(7.50m);
    }

    private static UsageTariffVersion Tariff(
        string tenantId,
        string dataSource,
        int version,
        DateOnly validFrom,
        DateOnly? validUntil,
        decimal basePerQuery = 0m,
        decimal perRow = 0m,
        decimal perMib = 0m,
        decimal perComputeSecond = 0m,
        decimal perAnalysisCost = 0m,
        decimal sourceCostFactor = 0m,
        string? sourceCostUnit = null,
        string unitLabel = "credits",
        short computeBasis = 0) =>
        new(tenantId, dataSource, version, validFrom, validUntil,
            basePerQuery, perRow, perMib, perComputeSecond, perAnalysisCost,
            sourceCostFactor, sourceCostUnit, unitLabel, computeBasis,
            "admin", Created, "test");

    private static UsageTariffRowInput Row(
        string tenantId,
        DateOnly day,
        string dataSource,
        bool hasGrant,
        long queries = 1,
        long rows = 0,
        long bytes = 0,
        long durationMs = 0,
        long analysisCost = 0,
        long sourceComputeMs = 0,
        long sourceComputeMeasured = 0,
        long sourceCostMicros = 0) =>
        new(tenantId, day, dataSource, hasGrant, queries, rows, bytes, durationMs, analysisCost,
            sourceComputeMs, sourceComputeMeasured, sourceCostMicros);
}
