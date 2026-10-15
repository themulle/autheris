namespace Autheris.Application.Analytics;

using System;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;

/// <summary>
/// Calculates and enforces query complexity and cardinality bounds for governed aggregations (#28).
/// </summary>
public static class AggregationCostCalculator
{
    public const double GroupByColumnWeight = 10.0;
    public const double AggregateSelectionWeight = 5.0;
    public const double CountDistinctWeight = 15.0;

    public static double CalculateCost(AggregationQuery query, AggregationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var opts = options ?? new AggregationOptions();

        double cost = opts.AggregateFieldCostWeight > 0
            ? opts.AggregateFieldCostWeight
            : AggregationOptions.DefaultAggregateFieldCostWeight;

        int groupByCount = query.GroupBy?.Count ?? 0;
        cost += groupByCount * GroupByColumnWeight;

        if (query.Aggregates is { Count: > 0 })
        {
            foreach (var agg in query.Aggregates)
            {
                cost += agg.Function == AggregateFunction.CountDistinct
                    ? CountDistinctWeight
                    : AggregateSelectionWeight;
            }
        }
        else
        {
            cost += AggregateSelectionWeight;
        }

        return cost;
    }

    public static void ValidateBounds(AggregationQuery query, AggregationOptions? options = null, double? maxAllowedCost = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var opts = options ?? new AggregationOptions();

        if (!opts.Enabled)
        {
            throw new GatewayForbiddenException("Governed aggregation queries are disabled by configuration.");
        }

        int groupByCount = query.GroupBy?.Count ?? 0;
        if (groupByCount > opts.MaxGroupByColumns)
        {
            throw new ArgumentException(
                $"Aggregation query specifies {groupByCount} GROUP BY columns, exceeding the maximum allowed ({opts.MaxGroupByColumns}).",
                nameof(query));
        }

        int aggregateCount = query.Aggregates?.Count ?? 0;
        if (aggregateCount > opts.MaxAggregates)
        {
            throw new ArgumentException(
                $"Aggregation query specifies {aggregateCount} aggregate projections, exceeding the maximum allowed ({opts.MaxAggregates}).",
                nameof(query));
        }

        if (query.Limit is <= 0)
        {
            throw new ArgumentException("Aggregation limit must be positive when specified.", nameof(query));
        }

        if (maxAllowedCost is > 0)
        {
            var cost = CalculateCost(query, opts);
            if (cost > maxAllowedCost.Value)
            {
                throw new CostLimitExceededException(
                    $"Aggregation query cost ({cost:F1}) exceeds the maximum allowed query cost ({maxAllowedCost.Value:F1}).");
            }
        }
    }
}
