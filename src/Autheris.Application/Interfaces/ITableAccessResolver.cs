using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Interfaces;

/// <summary>Metadata and effective access decision of one table for one caller (no audit, no data read).</summary>
public sealed record ResolvedTableAccess(
    TableMetadata Metadata,
    TableAccessDecision Decision,
    TenantId Tenant,
    Sid UserSid,
    ClaimsPrincipal Principal);

/// <summary>
/// G5: The single place that turns a caller and a table into an access decision (consents, decision cache, Casbin ABAC,
/// row filters). Implemented by GatewayExecutionService; GraphQL tree queries use it per table and request.
/// </summary>
public interface ITableAccessResolver
{
    /// <summary>Throws for unauthenticated callers and unknown tables; returns denied decisions instead of throwing.</summary>
    Task<ResolvedTableAccess> ResolveTableAccessAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        IReadOnlyList<string>? requestedFields,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        CancellationToken ct = default);
}
