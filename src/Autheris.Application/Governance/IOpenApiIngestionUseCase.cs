namespace Autheris.Application.Governance;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Domain.Common;

public enum OpenApiIngestionOutcomeKind
{
    /// <summary>Missing role or a domain outside the caller's tenant.</summary>
    Forbidden,

    /// <summary>The specification was not ingested; <see cref="OpenApiIngestionOutcome.Result"/> carries the reason.</summary>
    Rejected,

    Ingested
}

public sealed record OpenApiIngestionOutcome(OpenApiIngestionOutcomeKind Kind, OpenApiIngestionResult? Result = null);

/// <summary>
/// WP-ARC-B-07: authorizes (role, tenant domain), ingests an OpenAPI specification and writes the success audit.
/// The endpoint keeps the bounded body read and the response mapping.
/// </summary>
public interface IOpenApiIngestionUseCase
{
    /// <summary>
    /// The role and domain checks of <see cref="IngestAsync"/> without ingesting, so the endpoint can reject a caller before it reads
    /// the (up to 20 MB) body. Returns false when the caller is forbidden.
    /// </summary>
    bool IsAuthorized(ClaimsPrincipal user, TenantId callerTenant, bool isClusterAdmin, string? domain);

    Task<OpenApiIngestionOutcome> IngestAsync(
        ClaimsPrincipal user,
        TenantId callerTenant,
        bool isClusterAdmin,
        string? domain,
        string? baseUrl,
        string json,
        string traceId,
        CancellationToken ct);
}
