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
