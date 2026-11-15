namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>Input measures for pricing one aggregated usage row or individual fact.</summary>
public readonly record struct UsageTariffRowInput(
    string TenantId,
    DateOnly DayUtc,
    string DataSource,
    bool HasGrant,
    long Queries,
    long RowsReturned,
    long Bytes,
    long DurationMs,
    long AnalysisCost,
    long SourceComputeMs,
    long SourceComputeMeasured,
    long SourceCostMicros)
{
    public static UsageTariffRowInput FromReportRow(string tenantId, DateOnly dayUtc, UsageReportRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new UsageTariffRowInput(
            tenantId,
            dayUtc,
            row.DataSource ?? string.Empty,
            row.AllowCount > 0 || !string.IsNullOrWhiteSpace(row.GrantId),
            row.Queries,
            row.RowsReturned,
            row.Bytes,
            row.DurationMs,
            row.AnalysisCost,
            row.SourceComputeMs,
            row.SourceComputeMeasured,
            row.SourceCostMicros);
    }
}

/// <summary>Deterministic cost breakdown for a single <see cref="UsageTariffRowInput"/>.</summary>
public sealed record UsageTariffRowCost(
    decimal RawCostUnits,
    decimal CostUnits,
    string? UnitLabel,
    string? AppliedVersionId,
    bool TariffMissing);

/// <summary>Aggregated cost bucket for a single currency/unit label.</summary>
public sealed record UsageCostBucket(
    decimal CostUnits,
    string UnitLabel,
    IReadOnlyList<string> TariffVersions);

/// <summary>Rolled-up cost result grouped by <c>UnitLabel</c> (different labels are never summed).</summary>
public sealed record UsageCostSummary(
    IReadOnlyList<UsageCostBucket> Buckets,
    bool TariffMissing);

/// <summary>Pure deterministic linear tariff calculator.</summary>
public static class UsageTariffCalculator
{
    private const decimal BytesPerMib = 1_048_576m;
    private const decimal MillisPerSecond = 1_000m;
    private const decimal MicrosPerUnit = 1_000_000m;

    /// <summary>
    /// Resolves the tariff version active on <paramref name="dayUtc"/> for <paramref name="tenantId"/> and
    /// <paramref name="dataSource"/> in precedence order: <c>(t, s)</c>, <c>(t, *)</c>, <c>(*, s)</c>, <c>(*, *)</c>.
    /// </summary>
    public static UsageTariffVersion? ResolveTariff(
        string tenantId,
        DateOnly dayUtc,
        string dataSource,
        IReadOnlyList<UsageTariffVersion> tariffs)
    {
        ArgumentNullException.ThrowIfNull(tariffs);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(dataSource))
        {
            return null;
        }

        return FindMatch(tenantId, dataSource, dayUtc, tariffs)
            ?? FindMatch(tenantId, "*", dayUtc, tariffs)
            ?? FindMatch("*", dataSource, dayUtc, tariffs)
            ?? FindMatch("*", "*", dayUtc, tariffs);
    }

    /// <summary>Calculates deterministic cost units for one row, rounding only at output to 6 decimal places.</summary>
    public static UsageTariffRowCost CalculateRow(
        in UsageTariffRowInput input,
        IReadOnlyList<UsageTariffVersion> tariffs)
    {
        ArgumentNullException.ThrowIfNull(tariffs);

        // Only rows with a grant (allowed reads / procedure executions) are priced; denies & no-grant requests cost 0.
        if (!input.HasGrant)
        {
            return new UsageTariffRowCost(0m, 0m, null, null, TariffMissing: false);
        }

        var tariff = ResolveTariff(input.TenantId, input.DayUtc, input.DataSource, tariffs);
        if (tariff is null)
        {
            return new UsageTariffRowCost(0m, 0m, null, null, TariffMissing: true);
        }

        decimal computeSeconds = (tariff.ComputeBasis == 0 && input.Queries > 0 && input.SourceComputeMeasured == input.Queries)
            ? input.SourceComputeMs / MillisPerSecond
            : input.DurationMs / MillisPerSecond;

        decimal rawCost = (tariff.BasePerQuery * input.Queries)
            + (tariff.PerRow * input.RowsReturned)
            + (tariff.PerMib * input.Bytes / BytesPerMib)
            + (tariff.PerComputeSecond * computeSeconds)
            + (tariff.PerAnalysisCost * input.AnalysisCost)
            + (tariff.SourceCostFactor * input.SourceCostMicros / MicrosPerUnit);

        decimal rounded = RoundCost(rawCost);
        string versionId = FormatVersionId(tariff);
        return new UsageTariffRowCost(rawCost, rounded, tariff.UnitLabel, versionId, TariffMissing: false);
    }

    /// <summary>
    /// Prices each input row and aggregates costs per <c>UnitLabel</c>. Unrounded values are summed before rounding
    /// once at output to 6 decimal places. Different <c>UnitLabel</c> values are never summed.
    /// </summary>
    public static UsageCostSummary CalculateSummary(
        IEnumerable<UsageTariffRowInput> rows,
        IReadOnlyList<UsageTariffVersion> tariffs)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(tariffs);

        var totalsByLabel = new Dictionary<string, (decimal RawSum, HashSet<string> Versions)>(StringComparer.Ordinal);
        bool tariffMissing = false;

        foreach (var row in rows)
        {
            var item = CalculateRow(row, tariffs);
            if (item.TariffMissing)
            {
                tariffMissing = true;
                continue;
            }

            if (item.UnitLabel is null || item.AppliedVersionId is null)
            {
                continue;
            }

            if (!totalsByLabel.TryGetValue(item.UnitLabel, out var bucket))
            {
                bucket = (0m, new HashSet<string>(StringComparer.Ordinal));
            }

            bucket.Versions.Add(item.AppliedVersionId);
            totalsByLabel[item.UnitLabel] = (bucket.RawSum + item.RawCostUnits, bucket.Versions);
        }

        var buckets = new List<UsageCostBucket>(totalsByLabel.Count);
        foreach (var kvp in totalsByLabel)
        {
            var versions = new List<string>(kvp.Value.Versions);
            versions.Sort(StringComparer.Ordinal);
            buckets.Add(new UsageCostBucket(RoundCost(kvp.Value.RawSum), kvp.Key, versions));
        }

        buckets.Sort((a, b) => string.CompareOrdinal(a.UnitLabel, b.UnitLabel));
        return new UsageCostSummary(buckets, tariffMissing);
    }

    public static decimal RoundCost(decimal rawCost) =>
        Math.Round(rawCost, 6, MidpointRounding.AwayFromZero);

    public static string FormatVersionId(UsageTariffVersion tariff)
    {
        ArgumentNullException.ThrowIfNull(tariff);
        return string.Create(CultureInfo.InvariantCulture, $"{tariff.TenantId}/{tariff.DataSource}/v{tariff.Version}");
    }

    private static UsageTariffVersion? FindMatch(
        string tenantId,
        string dataSource,
        DateOnly dayUtc,
        IReadOnlyList<UsageTariffVersion> tariffs)
    {
        UsageTariffVersion? best = null;
        for (int i = 0; i < tariffs.Count; i++)
        {
            var candidate = tariffs[i];
            if (!string.Equals(candidate.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.DataSource, dataSource, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidate.ValidFrom > dayUtc || (candidate.ValidUntil.HasValue && dayUtc >= candidate.ValidUntil.Value))
            {
                continue;
            }

            if (best is null || candidate.Version > best.Version)
            {
                best = candidate;
            }
        }

        return best;
    }
}
