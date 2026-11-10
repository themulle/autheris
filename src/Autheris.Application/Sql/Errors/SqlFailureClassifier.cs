namespace Autheris.Application.Sql.Errors;

using System;
using System.Data.Common;
using System.Reflection;
using System.Security;
using System.Text.RegularExpressions;
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

public sealed record SqlFailure(
    SqlFailureKind Kind,
    string FixedMessage,
    string? Code = null,
    int? Line = null,
    int? Column = null,
    Exception? Exception = null);

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

        var failure = ClassifyCore(ex);
        int? line = failure.Line;
        int? col = failure.Column;

        if (line is null || col is null)
        {
            var loc = ExtractErrorLocation(ex);
            if (loc is not null)
            {
                line ??= loc.Value.LineNumber;
                col ??= loc.Value.ColumnNumber;
            }
        }

        return failure with
        {
            Line = line,
            Column = col,
            Exception = ex
        };
    }

    private static SqlFailure ClassifyCore(Exception ex)
    {
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
                return new SqlFailure(SqlFailureKind.InvalidQuery, mapped.Title, mapped.Code, mapped.Line, mapped.Column);
            }

            if (mapped.Status >= 500)
            {
                return new SqlFailure(SqlFailureKind.Internal, GenericServerErrorMessage, mapped.Code);
            }

            return new SqlFailure(SqlFailureKind.InvalidQuery, mapped.Title, mapped.Code, mapped.Line, mapped.Column);
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

    public static (int LineNumber, int ColumnNumber)? ExtractErrorLocation(Exception? ex)
    {
        if (ex is null) return null;

        for (var (current, depth) = (ex, 0); current is not null && depth < 8; current = current.InnerException, depth++)
        {
            if (current is TrinoSqlEngine.SqlSyntaxErrorException syn && syn.Line > 0 && syn.Column > 0)
            {
                return (syn.Line, syn.Column);
            }

            if (TryExtractFromObject(current, out var loc))
            {
                return loc;
            }

            if (current is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    var candidate = ExtractErrorLocation(inner);
                    if (candidate is not null) return candidate;
                }
            }
        }

        return null;
    }

    private static bool TryExtractFromObject(object obj, out (int LineNumber, int ColumnNumber) loc)
    {
        loc = default;
        int? line = GetNumericMember(obj, "LineNumber") ??
                    GetNumericMember(obj, "Line") ??
                    GetNumericMember(obj, "line") ??
                    GetNumericMember(obj, "lineNumber");

        int? col = GetNumericMember(obj, "ColumnNumber") ??
                   GetNumericMember(obj, "Column") ??
                   GetNumericMember(obj, "column") ??
                   GetNumericMember(obj, "columnNumber");

        if (col is null)
        {
            int? charPos = GetNumericMember(obj, "CharPositionInLine") ??
                           GetNumericMember(obj, "charPositionInLine") ??
                           GetNumericMember(obj, "CharPosition") ??
                           GetNumericMember(obj, "charPosition");
            if (charPos is not null)
            {
                col = charPos.Value >= 0 ? charPos.Value + 1 : 1;
            }
        }

        if (line is null || col is null)
        {
            var nestedLocation = GetObjectMember(obj, "ErrorLocation") ??
                                  GetObjectMember(obj, "errorLocation") ??
                                  GetObjectMember(obj, "Location") ??
                                  GetObjectMember(obj, "location") ??
                                  GetObjectMember(obj, "OffendingToken") ??
                                  GetObjectMember(obj, "Token");
            if (nestedLocation is not null && !ReferenceEquals(nestedLocation, obj))
            {
                line ??= GetNumericMember(nestedLocation, "LineNumber") ??
                         GetNumericMember(nestedLocation, "Line") ??
                         GetNumericMember(nestedLocation, "line");

                col ??= GetNumericMember(nestedLocation, "ColumnNumber") ??
                        GetNumericMember(nestedLocation, "Column") ??
                        GetNumericMember(nestedLocation, "column");

                if (col is null)
                {
                    int? charPos = GetNumericMember(nestedLocation, "CharPositionInLine") ??
                                   GetNumericMember(nestedLocation, "charPositionInLine");
                    if (charPos is not null)
                    {
                        col = charPos.Value >= 0 ? charPos.Value + 1 : 1;
                    }
                }
            }
        }

        if ((line is null || col is null) && obj is Exception exObj && exObj.Data.Count > 0)
        {
            line ??= GetIntFromData(exObj.Data, "LineNumber") ??
                     GetIntFromData(exObj.Data, "Line") ??
                     GetIntFromData(exObj.Data, "line");

            col ??= GetIntFromData(exObj.Data, "ColumnNumber") ??
                    GetIntFromData(exObj.Data, "Column") ??
                    GetIntFromData(exObj.Data, "column");

            if (col is null)
            {
                int? charPos = GetIntFromData(exObj.Data, "CharPositionInLine") ??
                               GetIntFromData(exObj.Data, "charPositionInLine");
                if (charPos is not null)
                {
                    col = charPos.Value >= 0 ? charPos.Value + 1 : 1;
                }
            }
        }

        if ((line is null || col is null) && obj is Exception exWithMsg && !string.IsNullOrWhiteSpace(exWithMsg.Message))
        {
            var match = MessageLocationRegex.Match(exWithMsg.Message);
            if (match.Success &&
                int.TryParse(match.Groups[1].ValueSpan, out var mLine) &&
                int.TryParse(match.Groups[2].ValueSpan, out var mCol))
            {
                line ??= mLine;
                col ??= mCol <= 0 ? 1 : mCol;
            }
        }

        if (line is not null && col is not null && line.Value > 0 && col.Value > 0)
        {
            loc = (line.Value, col.Value);
            return true;
        }

        return false;
    }

    private static readonly Regex MessageLocationRegex = new(@"(?:line\s+|at\s+line\s+)(\d+)[:,\s]+(?:col(?:umn)?\s+)?(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static int? GetNumericMember(object obj, string name)
    {
        var type = obj.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null && prop.CanRead)
        {
            try
            {
                var val = prop.GetValue(obj);
                if (val is int i) return i;
                if (val is long l) return (int)l;
                if (val is short s) return (int)s;
            }
            catch { }
        }

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field is not null)
        {
            try
            {
                var val = field.GetValue(obj);
                if (val is int i) return i;
                if (val is long l) return (int)l;
                if (val is short s) return (int)s;
            }
            catch { }
        }

        return null;
    }

    private static object? GetObjectMember(object obj, string name)
    {
        var type = obj.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null && prop.CanRead)
        {
            try { return prop.GetValue(obj); } catch { }
        }
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field is not null)
        {
            try { return field.GetValue(obj); } catch { }
        }
        return null;
    }

    private static int? GetIntFromData(System.Collections.IDictionary data, string key)
    {
        if (data.Contains(key))
        {
            var val = data[key];
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is short s) return (int)s;
            if (val is string str && int.TryParse(str, out var parsed)) return parsed;
        }
        return null;
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
