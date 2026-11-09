namespace Autheris.Application.Sql.Errors;

using System;
using System.Data.Common;
using System.Security;
using Autheris.Domain.Exceptions;
using TrinoSqlEngine.Ast.Emit;

public enum SqlFailureKind
{
    InvalidQuery,
    PolicyDenied,
    TransportDenied,
    Timeout,
    Throttled,
    NotSupported,
    Canceled,
    Internal
}

public sealed record SqlFailure(SqlFailureKind Kind, string FixedMessage, string? Code = null);

public static class SqlFailureClassifier
{
    public const string GenericServerErrorMessage = "The SQL statement could not be executed. Contact support with the trace id.";
    public const string GenericBadRequestMessage = "The SQL request is invalid (syntax error, empty statement, invalid parameter or size limit exceeded). " +
        "WebSQL accepts ANSI/Trino-style SELECT statements: use LIMIT n instead of TOP n and name tables as <dataSource>.<schema>.<table> or <schema>.<table>.";
    public const string GenericTimeoutMessage = "The SQL statement exceeded the execution time limit. Narrow the query (filter, fewer columns, LIMIT) or contact support with the trace id.";
    public const string GenericAccessDeniedMessage = "Access denied.";
    public const string GenericThrottledMessage = "Too many concurrent requests. Retry later.";
    public const string GenericNotSupportedMessage = "The requested operation is not supported.";
    public const string GenericNotImplementedMessage = "The requested feature or data source capability is not implemented.";
    public const string GenericCanceledMessage = "Query execution timed out or was canceled.";
    public const string ClientCanceledMessage = "Query execution was canceled by the client.";

    public static SqlFailure Classify(Exception? ex)
    {
        if (ex == null)
        {
            return new SqlFailure(SqlFailureKind.Internal, GenericServerErrorMessage, "INTERNAL_ERROR");
        }

        // 1. Check compiler/governed error mapper
        if (SqlCompilerErrorMapper.TryMap(ex, out var mapped))
        {
            if (string.Equals(mapped.Code, TransportNotAllowedException.Code, StringComparison.Ordinal))
            {
                return new SqlFailure(SqlFailureKind.TransportDenied, mapped.Title, mapped.Code);
            }

            if (mapped.Status == 504 || string.Equals(mapped.Code, GovernedSqlErrorCodes.Timeout, StringComparison.Ordinal))
            {
                return new SqlFailure(SqlFailureKind.Timeout, mapped.Title, mapped.Code);
            }

            if (mapped.Status == 429 || string.Equals(mapped.Code, GovernedSqlErrorCodes.RateLimitExceeded, StringComparison.Ordinal))
            {
                return new SqlFailure(SqlFailureKind.Throttled, mapped.Title, mapped.Code);
            }

            if (mapped.Status == 501 ||
                string.Equals(mapped.Code, GovernedSqlErrorCodes.PagingUnsupported, StringComparison.Ordinal) ||
                string.Equals(mapped.Code, GovernedSqlErrorCodes.ConstructNotSupported, StringComparison.Ordinal) ||
                string.Equals(mapped.Code, GovernedSqlErrorCodes.DialectNotSupported, StringComparison.Ordinal))
            {
                return new SqlFailure(SqlFailureKind.NotSupported, mapped.Title, mapped.Code);
            }

            if (mapped.Status == 403)
            {
                return new SqlFailure(SqlFailureKind.PolicyDenied, mapped.Title, mapped.Code);
            }

            if (mapped.Status == 400 || mapped.Status == 422 || mapped.Status == 409)
            {
                return new SqlFailure(SqlFailureKind.InvalidQuery, mapped.Title, mapped.Code);
            }

            if (mapped.Status >= 500)
            {
                return new SqlFailure(SqlFailureKind.Internal, GenericServerErrorMessage, mapped.Code);
            }

            return new SqlFailure(SqlFailureKind.InvalidQuery, mapped.Title, mapped.Code);
        }

        // 2. Exception type branches
        if (ex is TransportNotAllowedException)
        {
            return new SqlFailure(SqlFailureKind.TransportDenied, "Access to the data source is not allowed over this transport.", TransportNotAllowedException.Code);
        }

        if (ex is GatewayThrottledException)
        {
            return new SqlFailure(SqlFailureKind.Throttled, GenericThrottledMessage, "TOO_MANY_REQUESTS");
        }

        if (ex is GatewayNotImplementedException)
        {
            return new SqlFailure(SqlFailureKind.NotSupported, GenericNotImplementedMessage, "NOT_IMPLEMENTED");
        }

        if (ex is NotSupportedException)
        {
            return new SqlFailure(SqlFailureKind.NotSupported, GenericNotSupportedMessage, "NOT_IMPLEMENTED");
        }

        if (ex is GatewaySecurityException or SecurityException)
        {
            return new SqlFailure(SqlFailureKind.PolicyDenied, GenericAccessDeniedMessage, "ACCESS_DENIED");
        }

        if (IsExecutionTimeout(ex))
        {
            return new SqlFailure(SqlFailureKind.Timeout, GenericTimeoutMessage, GovernedSqlErrorCodes.Timeout);
        }

        if (ex is OperationCanceledException)
        {
            return new SqlFailure(SqlFailureKind.Canceled, GenericCanceledMessage, null);
        }

        if (ex is ArgumentException or GatewayInvalidQueryException)
        {
            return new SqlFailure(SqlFailureKind.InvalidQuery, GenericBadRequestMessage, "BAD_REQUEST");
        }

        return new SqlFailure(SqlFailureKind.Internal, GenericServerErrorMessage, "INTERNAL_ERROR");
    }

    public static bool IsExecutionTimeout(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is TimeoutException ||
                (current is DbException && current.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
