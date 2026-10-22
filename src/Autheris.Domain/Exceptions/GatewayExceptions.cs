using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Domain.Exceptions;

public class GatewaySecurityException : Exception
{
    public string ErrorCode { get; }

    public GatewaySecurityException(string message, string errorCode = "FORBIDDEN")
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public GatewaySecurityException(string message, string errorCode, Exception? innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

public sealed class GatewayUnauthorizedException : GatewaySecurityException
{
    public GatewayUnauthorizedException(string message = "Authentication is required to query tables.")
        : base(message, "UNAUTHORIZED")
    {
    }

    public GatewayUnauthorizedException(string message, string errorCode)
        : base(message, errorCode)
    {
    }
}

public sealed class GatewayForbiddenException : GatewaySecurityException
{
    public GatewayForbiddenException(string message = "Access denied.")
        : base(message, "FORBIDDEN")
    {
    }

    public GatewayForbiddenException(string message, string errorCode)
        : base(message, errorCode)
    {
    }
}

public class TableNotFoundException : GatewaySecurityException
{
    public TableIdentifier Table { get; }

    public TableNotFoundException(TableIdentifier table)
        : base($"Table '{table}' does not exist.", "NOT_FOUND")
    {
        Table = table;
    }
}

/// <summary>
/// O1/O2: The request names something the caller cannot use (unknown column, denied column, invalid option).
/// Unknown and denied columns produce the same message, so the error is not an existence oracle.
/// Not a <see cref="GatewaySecurityException"/>: it is a client error (400), not an access decision.
/// </summary>
public class GatewayInvalidQueryException : Exception
{
    public string ErrorCode { get; }

    public GatewayInvalidQueryException(string message, string errorCode = "INVALID_QUERY_OPTION")
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public GatewayInvalidQueryException(string message, string errorCode, Exception? innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>O10: Too many concurrent reads of the same table by the same user (429 with Retry-After).</summary>
public sealed class GatewayThrottledException : Exception
{
    public string ErrorCode => "TOO_MANY_REQUESTS";
    public int RetryAfterSeconds { get; }

    public GatewayThrottledException(int retryAfterSeconds)
        : base("Too many concurrent requests for this table. Retry later.")
    {
        RetryAfterSeconds = Math.Max(1, retryAfterSeconds);
    }

    public GatewayThrottledException(string message, int retryAfterSeconds = 5)
        : base(message)
    {
        RetryAfterSeconds = Math.Max(1, retryAfterSeconds);
    }
}

/// <summary>
/// O12: The database driver cannot materialize a column type (e.g. SQL Server geography/hierarchyid without
/// Microsoft.SqlServer.Types). The message names the column so the client can exclude it with $select; the driver
/// error stays in the inner exception and is only logged.
/// </summary>
public sealed class GatewayUnsupportedColumnTypeException : Exception
{
    public string ErrorCode => "UNSUPPORTED_COLUMN_TYPE";
    public string ColumnName { get; }
    public string DataTypeName { get; }

    public GatewayUnsupportedColumnTypeException(string columnName, string dataTypeName, Exception? innerException = null)
        : base($"The column '{columnName}' has the database type '{dataTypeName}', which this service cannot return. Exclude it with $select.", innerException)
    {
        ColumnName = columnName;
        DataTypeName = dataTypeName;
    }
}

public sealed class ResourceGroupExhaustedException : GatewaySecurityException
{
    public Model.ResourceGroupTier Tier { get; }
    public string RejectionReason { get; }

    public ResourceGroupExhaustedException(Model.ResourceGroupTier tier, string rejectionReason)
        : base($"Resource group '{tier}' request rejected: {rejectionReason}.", rejectionReason == "QueueFull" ? "RESOURCE_GROUP_QUEUE_FULL" : "RESOURCE_GROUP_TIMEOUT")
    {
        Tier = tier;
        RejectionReason = rejectionReason;
    }
}

/// <summary>
/// S-2: Explicit 501 Not Implemented exception with standardized, non-leaking message (SEC M-10).
/// </summary>
public sealed class GatewayNotImplementedException : Exception
{
    public string ErrorCode => "NOT_IMPLEMENTED";

    public GatewayNotImplementedException(string message = "The requested feature or data source capability is not implemented.", Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class ColumnNotReleasedException : GatewaySecurityException
{
    public string ColumnName { get; }

    public ColumnNotReleasedException(string columnName)
        : base($"Column '{columnName}' is not released for this principal.", "COLUMN_NOT_RELEASED")
    {
        ColumnName = columnName;
    }
}

public sealed class FilterOnMaskedColumnException : GatewaySecurityException
{
    public string ColumnName { get; }

    public FilterOnMaskedColumnException(string columnName)
        : base($"Filtering on masked column '{columnName}' is not permitted.", "FILTER_ON_MASKED_COLUMN")
    {
        ColumnName = columnName;
    }
}

public sealed class CostLimitExceededException : GatewaySecurityException
{
    public CostLimitExceededException(string message = "Query exceeded maximum allowed execution cost.")
        : base(message, "COST_LIMIT")
    {
    }
}

public sealed class RowLimitExceededException : GatewaySecurityException
{
    public RowLimitExceededException(string message = "Query requested more rows than permitted by security policy.")
        : base(message, "ROW_LIMIT")
    {
    }
}

public sealed class GatewayRateLimitException : GatewaySecurityException
{
    public GatewayRateLimitException(string message = "Rate limit exceeded.")
        : base(message, "RATE_LIMIT")
    {
    }
}


/// <summary>
/// Issue #94 (SD-3): the persisted key/index definition of a table is invalid, so its metadata is unavailable, never "absent".
/// It is a <see cref="TableNotFoundException"/> on purpose: every read transport (REST, WebSQL, OData, GraphQL, MCP, CDC) already
/// maps that exception to its not-found/denied answer, so an unavailable table is indistinguishable from an unknown one (same
/// status, same body, same message) and fails closed everywhere, including in callers added later. Ingestion and write paths catch
/// this type first (it must never be treated as "new table"). The message is the not-found text and names only the table id.
/// </summary>
public sealed class TableMetadataUnavailableException(TableIdentifier table) : TableNotFoundException(table);

/// <summary>
/// Issue #151: a relation write was rejected (invalid key columns, unknown or ambiguous table). The message carries the
/// relation name and column identifiers only.
/// </summary>
public sealed class InvalidRelationException(RelationKeyViolation? violation, string message) : Exception(message)
{
    public RelationKeyViolation? Violation { get; } = violation;
}
