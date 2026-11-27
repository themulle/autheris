namespace Autheris.Application.Mcp.Services;

using Autheris.Application.Sql.Errors;
using Autheris.Domain.Exceptions;
using TrinoSqlEngine.Ast.Emit;

public static partial class McpToolError
{
    /// <summary>
    /// Maps a failure of a dataset query (filter, select, orderBy, access) to the code the HTTP dataset endpoint answers for the same input.
    /// A denied, hidden or unknown dataset is one answer. False for cancellation, timeouts, transport and concurrency failures (they
    /// have their own results) and for anything unmapped (the tool boundary answers those).
    /// </summary>
    public static bool TryMapQueryDatasetFailure(Exception ex, out string envelope)
    {
        envelope = string.Empty;
        if (ex is TimeoutException or TransportNotAllowedException or ScopeConcurrencyExceededException)
        {
            return false;
        }

        if (ex is TableNotFoundException or GatewayForbiddenException or GatewayUnauthorizedException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            envelope = Serialize(McpErrorCodes.DatasetNotFound, McpErrorCodes.FixedMessage(McpErrorCodes.DatasetNotFound));
            return true;
        }

        if (ex is Autheris.Domain.Model.RequiredFilterColumnMissingException)
        {
            envelope = Serialize(McpErrorCodes.RequiredFilterColumnMissing, string.Empty);
            return true;
        }

        if (SqlCompilerErrorMapper.TryMap(ex, out var mapped))
        {
            SqlCompilerErrorMapper.RecordOutcome(mapped);
            envelope = mapped.Code switch
            {
                GovernedSqlErrorCodes.SyntaxError => Serialize(McpErrorCodes.InvalidQuery, McpErrorCodes.FixedMessage(McpErrorCodes.InvalidQuery)),
                GovernedSqlErrorCodes.PolicyDenied => Serialize(McpErrorCodes.DatasetNotFound, McpErrorCodes.FixedMessage(McpErrorCodes.DatasetNotFound)),
                _ => Serialize(FromSql(ex, mapped))
            };
            return true;
        }

        // After the mapping: the parser reports a syntax error as an OperationCanceledException subtype, a real cancellation maps to nothing.
        if (ex is OperationCanceledException)
        {
            return false;
        }

        if (ex is ArgumentException)
        {
            envelope = Serialize(McpErrorCodes.InvalidQuery, McpErrorCodes.FixedMessage(McpErrorCodes.InvalidQuery));
            return true;
        }

        return false;
    }

    /// <summary>Maps a failure of a semantic metric query: unknown, denied and unresolvable metrics are one answer.</summary>
    public static string MapMetricQueryFailure(Exception ex)
    {
        switch (ex)
        {
            case KeyNotFoundException or TableNotFoundException or GatewayForbiddenException:
                return Serialize(McpErrorCodes.MetricNotFound, string.Empty);
            case GatewayInvalidQueryException invalid:
                // An unknown metric must not differ from a denied one.
                return Serialize(invalid.ErrorCode == Autheris.Application.Analytics.AggregationErrorCodes.UnknownMetric
                    ? McpErrorCodes.MetricNotFound
                    : McpErrorCodes.IsCatalogueCode(invalid.ErrorCode) ? invalid.ErrorCode : McpErrorCodes.InvalidMetricQuery, string.Empty);
            case ArgumentException:
                return Serialize(McpErrorCodes.InvalidMetricQuery, string.Empty);
            case GatewaySecurityException security:
                return Serialize(McpErrorCodes.IsCatalogueCode(security.ErrorCode) ? security.ErrorCode : McpErrorCodes.Forbidden, string.Empty);
            default:
                return Serialize(McpErrorCodes.ExecutionFailed, string.Empty);
        }
    }
}
