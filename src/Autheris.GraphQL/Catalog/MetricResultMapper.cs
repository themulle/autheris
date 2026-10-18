using Autheris.Application.Analytics;

namespace Autheris.GraphQL.Catalog;

internal sealed record MetricGroupDto(
    IReadOnlyList<AggregateValueDto> Dimensions,
    long RowCount,
    bool IsMergedOther,
    IReadOnlyList<AggregateValueDto> Values);

/// <summary>Allow-list view of a metric definition: no filter template, type parameters or meta (SI-112-6, SEC-112-18).</summary>
internal sealed record MetricDefinitionDto(string Name, string Type, string? Label, string? Description, string? Unit);

/// <summary>The typed <c>queryMetrics</c> result. It deliberately has no suppressed-row count (#112 O-4, SEC-112-20).</summary>
internal sealed record MetricResultDto(
    IReadOnlyList<string> Metrics,
    string ManifestVersion,
    IReadOnlyList<MetricGroupDto> Groups,
    long Count,
    bool Suppressed,
    int SuppressedGroupsCount,
    int MinimumGroupSize,
    bool MergedIntoOther,
    bool Truncated,
    bool NoiseApplied,
    IReadOnlyList<MetricDefinitionDto> MetricDefinitions);

/// <summary>#112 A-8: pure mapping of a metric query result to the typed GraphQL shape (kinds come from the service).</summary>
internal static class MetricResultMapper
{
    public static MetricResultDto Map(MetricQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var groups = new List<MetricGroupDto>(result.Groups.Count);
        foreach (var row in result.Groups)
        {
            var dimensions = new List<AggregateValueDto>(result.DimensionValueKinds.Count);
            foreach (var (name, kind) in result.DimensionValueKinds)
            {
                // The merged OTHER row stands for many groups: no dimension value is ever filled (SEC-112-07).
                var raw = row.IsMergedOther ? null : Lookup(row.GroupKey, name);
                dimensions.Add(AggregateValueConverter.Convert(name, kind, raw, lenientString: true));
            }

            var values = new List<AggregateValueDto>(result.Metrics.Count);
            foreach (var metric in result.Metrics)
            {
                var kind = result.MetricValueKinds.TryGetValue(metric, out var known) ? known : AggregateValueKind.Decimal;
                values.Add(AggregateValueConverter.Convert(metric, kind, Lookup(row.Aggregates, metric)));
            }

            groups.Add(new MetricGroupDto(dimensions, row.Count, row.IsMergedOther, values));
        }

        var definitions = new List<MetricDefinitionDto>(result.Metrics.Count);
        foreach (var metric in result.Metrics)
        {
            if (result.MetricDefinitions.TryGetValue(metric, out var summary))
            {
                definitions.Add(new MetricDefinitionDto(summary.Name, summary.Type, summary.Label, summary.Description, summary.Unit));
            }
        }

        return new MetricResultDto(
            result.Metrics,
            result.ManifestVersion,
            groups,
            result.Totals?.Count ?? 0L,
            result.Suppressed,
            result.SuppressedGroupsCount,
            result.MinimumGroupSize,
            result.MergedIntoOther,
            result.Truncated(),
            result.Metadata.NoiseApplied,
            definitions);
    }

    private static bool Truncated(this MetricQueryResult result) => result.Metadata.Truncated;

    private static object? Lookup(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (values.TryGetValue(name, out var direct))
        {
            return direct;
        }

        foreach (var (k, v) in values)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }
}
