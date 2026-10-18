namespace Autheris.Application.Analytics;

/// <summary>
/// New error codes of the typed aggregation surface (#112). <c>AGGREGATION_COLUMN_UNAVAILABLE</c>, <c>AGGREGATION_ON_MASKED_COLUMN</c> and
/// <c>AGGREGATION_ROW_CAP_EXCEEDED</c> stay on their exception classes in <c>AggregationModels.cs</c>.
/// </summary>
public static class AggregationErrorCodes
{
    public const string AliasInvalid = "AGGREGATION_ALIAS_INVALID";
    public const string ArgumentInvalid = "AGGREGATION_ARGUMENT_INVALID";
    public const string InvalidFunctionForType = "AGGREGATION_INVALID_FUNCTION_FOR_TYPE";
    public const string LimitExceeded = "AGGREGATION_LIMIT_EXCEEDED";
    public const string OrderInvalid = "AGGREGATION_ORDER_INVALID";
    public const string ResultTypeMismatch = "AGGREGATION_RESULT_TYPE_MISMATCH";
    public const string UnknownMetric = "UNKNOWN_METRIC";
    public const string MetricFilterInvalid = "METRIC_FILTER_INVALID";
}
