using Autheris.Domain.Common;

namespace Autheris.Domain.Exceptions;

public class GatewaySecurityException : Exception
{
    public string ErrorCode { get; }

    public GatewaySecurityException(string message, string errorCode = "FORBIDDEN")
        : base(message)
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
}

public sealed class GatewayForbiddenException : GatewaySecurityException
{
    public GatewayForbiddenException(string message = "Access denied.")
        : base(message, "FORBIDDEN")
    {
    }
}

public sealed class TableNotFoundException : GatewaySecurityException
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
public sealed class GatewayInvalidQueryException : Exception
{
    public string ErrorCode => "INVALID_QUERY_OPTION";

    public GatewayInvalidQueryException(string message)
        : base(message)
    {
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

