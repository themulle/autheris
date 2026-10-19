namespace Autheris.GraphQL.Catalog;

/// <summary>
/// #112 A-1: every GraphQL name the typed aggregation surface adds for one table (derived from the table type name by this one helper,
/// so the schema model, the type module and the tests agree), plus the shared type names it reserves in the schema.
/// </summary>
internal readonly record struct AggregateTypeNames(
    string Column,
    string Input,
    string OrderBy,
    string Key,
    string Group,
    string Result,
    string Field)
{
    public const string AggregateFn = "AutherisAggregateFn";
    public const string ValueKind = "AutherisValueKind";
    public const string AggregateValue = "AutherisAggregateValue";
    public const string TypedFilter = "AutherisTypedFilter";
    public const string DimensionFilter = "AutherisDimensionFilter";
    public const string MetricFilter = "AutherisMetricFilter";
    public const string MetricOrderBy = "AutherisMetricOrderBy";
    public const string DimensionValue = "AutherisDimensionValue";
    public const string MetricGroup = "AutherisMetricGroup";
    public const string MetricDefinition = "AutherisMetricDefinition";
    public const string MetricQueryResult = "AutherisMetricQueryResult";
    public const string TimeBucketInterval = "TimeBucketInterval";

    /// <summary>Shared type names of the typed aggregation and metric surface; they join the schema model's reserved names.</summary>
    public static IReadOnlyList<string> SharedTypeNames { get; } =
    [
        AggregateFn, ValueKind, AggregateValue, TypedFilter, DimensionFilter, MetricFilter, MetricOrderBy,
        DimensionValue, MetricGroup, MetricDefinition, MetricQueryResult, TimeBucketInterval
    ];

    public static AggregateTypeNames For(string tableTypeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableTypeName);
        return new AggregateTypeNames(
            Column: tableTypeName + "_column",
            Input: tableTypeName + "_aggregate_input",
            OrderBy: tableTypeName + "_aggregate_order_by",
            Key: tableTypeName + "_aggregate_key",
            Group: tableTypeName + "_aggregate_group",
            Result: tableTypeName + "_aggregate_result",
            Field: tableTypeName + "_aggregate");
    }

    public string GroupByFields => Column.EndsWith("_column", StringComparison.Ordinal)
        ? Column[..^7] + "_group_by_fields"
        : Column + "_group_by_fields";

    public string TimeBucketInput => Column.EndsWith("_column", StringComparison.Ordinal)
        ? Column[..^7] + "_time_bucket_input"
        : Column + "_time_bucket_input";

    /// <summary>All names the table reserves besides its entity, filter and order-by types.</summary>
    public IEnumerable<string> All()
    {
        yield return Column;
        yield return GroupByFields;
        yield return TimeBucketInput;
        yield return Input;
        yield return OrderBy;
        yield return Key;
        yield return Group;
        yield return Result;
        yield return Field;
    }
}
