using Autheris.Application.Analytics;
using Autheris.Application.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using HotChocolate;

namespace Autheris.GraphQL.Catalog;

/// <summary>
/// #112 SEC-112-11 / C-11: the one place that builds typed aggregation errors. Every message is fixed, every code stable; no column,
/// table, alias or value of the request is echoed.
/// </summary>
internal static class AggregateErrors
{
    public const string AliasInvalidMessage = "The aggregate alias is not valid.";
    public const string ArgumentInvalidMessage = "The aggregation arguments are not valid.";
    public const string LimitExceededMessage = "The requested limit is above the allowed maximum.";
    public const string OrderInvalidMessage = "The aggregation ordering is not valid.";
    public const string InvalidFunctionMessage = "The aggregate function is not valid for the column type.";
    public const string ResultTypeMismatchMessage = "An aggregation result could not be converted to its declared type.";
    public const string UnknownMetricMessage = "The metric is not available.";
    public const string AccessDeniedMessage = "Access denied.";
    public const string UnauthorizedMessage = "Authentication is required.";
    public const string BudgetExhaustedMessage = "Privacy budget exhausted.";
    public const string DisabledMessage = "Governed aggregation queries are disabled by configuration.";
    public const string TooComplexMessage = "The aggregation is too complex.";
    public const string ThrottledMessage = "Too many requests.";
    public const string MaskedMessage = "A requested column is masked for the current caller and cannot be used in an aggregation.";

    public static GraphQLException Create(string code, string message) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode(code).Build());

    public static GraphQLException AliasInvalid() => Create(AggregationErrorCodes.AliasInvalid, AliasInvalidMessage);

    public static GraphQLException ArgumentInvalid() => Create(AggregationErrorCodes.ArgumentInvalid, ArgumentInvalidMessage);

    public static GraphQLException LimitExceeded() => Create(AggregationErrorCodes.LimitExceeded, LimitExceededMessage);

    public static GraphQLException OrderInvalid() => Create(AggregationErrorCodes.OrderInvalid, OrderInvalidMessage);

    public static GraphQLException InvalidFunctionForType() => Create(AggregationErrorCodes.InvalidFunctionForType, InvalidFunctionMessage);

    public static GraphQLException ResultTypeMismatch() => Create(AggregationErrorCodes.ResultTypeMismatch, ResultTypeMismatchMessage);
}

/// <summary>Maps engine and service failures to the fixed typed errors; unknown failures are not mapped (the error filter sanitises them).</summary>
internal static class AggregateFailureMapper
{
    public static GraphQLException? ForAggregate(Exception ex) => ex switch
    {
        GraphQLException g => g,
        PrivacyBudgetExhaustedException => AggregateErrors.Create("PRIVACY_BUDGET_EXHAUSTED", AggregateErrors.BudgetExhaustedMessage),
        MaskedAggregationException => AggregateErrors.Create(MaskedAggregationException.ErrorCodeConst, AggregateErrors.MaskedMessage),
        AggregationColumnUnavailableException => AggregateErrors.Create(AggregationColumnUnavailableException.ErrorCodeConst, AggregationColumnUnavailableException.FixedMessage),
        AggregationRowCapExceededException => AggregateErrors.Create(AggregationRowCapExceededException.ErrorCodeConst, AggregationRowCapExceededException.FixedMessage),
        GatewayUnauthorizedException => AggregateErrors.Create("UNAUTHORIZED", AggregateErrors.UnauthorizedMessage),
        GatewayForbiddenException or TableNotFoundException => AggregateErrors.Create("ACCESS_DENIED", AggregateErrors.AccessDeniedMessage),
        CostLimitExceededException => AggregateErrors.Create("QUERY_TOO_COMPLEX", AggregateErrors.TooComplexMessage),
        GatewayThrottledException => AggregateErrors.Create("TOO_MANY_REQUESTS", AggregateErrors.ThrottledMessage),
        GatewayInvalidQueryException or ArgumentException => AggregateErrors.ArgumentInvalid(),
        _ => ForDataAccess(ex)
    };

    public static GraphQLException? ForMetrics(Exception ex) => ex switch
    {
        GraphQLException g => g,
        // Anti-oracle (C-11, T-S-09): an unknown metric, another tenant's metric and a metric without consent look the same.
        System.Collections.Generic.KeyNotFoundException or TableNotFoundException or InvalidOperationException => AggregateErrors.Create(AggregationErrorCodes.UnknownMetric, AggregateErrors.UnknownMetricMessage),
        GatewayForbiddenException f when f.Message.Contains("disabled by configuration", StringComparison.Ordinal) =>
            AggregateErrors.Create("AGGREGATION_DISABLED", AggregateErrors.DisabledMessage),
        GatewayForbiddenException => AggregateErrors.Create(AggregationErrorCodes.UnknownMetric, AggregateErrors.UnknownMetricMessage),
        MetricFilterInvalidException => AggregateErrors.Create(AggregationErrorCodes.MetricFilterInvalid, MetricFilterInvalidException.FixedMessage),
        _ => ForAggregate(ex)
    };

    private static GraphQLException? ForDataAccess(Exception ex) => DataAccessErrorClassifier.Classify(ex) switch
    {
        DataAccessErrorKind.Timeout => AggregateErrors.Create("TIMEOUT", "The database query timed out."),
        DataAccessErrorKind.Unavailable => AggregateErrors.Create("UNAVAILABLE", "The database is currently unavailable."),
        _ => null
    };
}
