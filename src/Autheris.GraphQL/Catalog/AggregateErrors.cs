using Autheris.Application.Analytics;
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
