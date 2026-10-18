namespace Autheris.Application.Analytics;

using Autheris.Domain.Exceptions;

/// <summary>
/// A typed metric filter leaf declares a value kind that does not fit the resolved column (<c>METRIC_FILTER_INVALID</c>). The fixed
/// message never echoes the dimension name or the value (SEC-112-11).
/// </summary>
public sealed class MetricFilterInvalidException : GatewayInvalidQueryException
{
    public const string FixedMessage = "The metric filter does not fit the dimension type.";

    public MetricFilterInvalidException()
        : base(FixedMessage, AggregationErrorCodes.MetricFilterInvalid)
    {
    }
}
