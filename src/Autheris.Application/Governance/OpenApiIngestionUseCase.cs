namespace Autheris.Application.Governance;

using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>Default <see cref="IOpenApiIngestionUseCase"/>.</summary>
public sealed class OpenApiIngestionUseCase(IOpenApiIngestionService ingestionService, IAuditWriter auditWriter) : IOpenApiIngestionUseCase
{
    private static readonly IGatewayRoleEvaluator Roles = new GatewayRoleEvaluator();

    public bool HasPublishRole(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return HasRole(user);
    }

    private static bool HasRole(ClaimsPrincipal user) =>
        Roles.HasAnyRole(user, new[] { GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher });

    public bool IsAuthorized(ClaimsPrincipal user, TenantId callerTenant, bool isClusterAdmin, string? domain) =>
        TryResolveDomain(user, callerTenant, isClusterAdmin, domain, out _);

    public async Task<OpenApiIngestionOutcome> IngestAsync(
        ClaimsPrincipal user,
        TenantId callerTenant,
        bool isClusterAdmin,
        string? domain,
        string? baseUrl,
        string json,
        string traceId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(json);

        if (!TryResolveDomain(user, callerTenant, isClusterAdmin, domain, out var resolvedDomain))
        {
            return new OpenApiIngestionOutcome(OpenApiIngestionOutcomeKind.Forbidden);
        }

        var result = await ingestionService.IngestOpenApiJsonAsync(json, resolvedDomain, baseUrl, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return new OpenApiIngestionOutcome(OpenApiIngestionOutcomeKind.Rejected, result);
        }

        // The catalog is already committed: the success audit must not be skipped because the client went away.
        await auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = callerTenant,
            EventType = "OPENAPI_CATALOG_INGESTED",
            ActorSid = user.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = resolvedDomain,
            Decision = "ALLOW",
            TraceId = traceId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                domain = resolvedDomain,
                serviceTitle = result.ServiceTitle,
                ingestedTablesCount = result.IngestedTablesCount,
                ingestedColumnsCount = result.IngestedColumnsCount,
                ingestedTableNames = result.IngestedTableNames,
                warnings = result.Warnings
            })
        }, CancellationToken.None).ConfigureAwait(false);

        return new OpenApiIngestionOutcome(OpenApiIngestionOutcomeKind.Ingested, result);
    }

    private static bool TryResolveDomain(ClaimsPrincipal user, TenantId callerTenant, bool isClusterAdmin, string? domain, out string resolved)
    {
        resolved = string.Empty;
        if (!HasRole(user))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(domain))
        {
            var requestedDomain = domain.Trim();
            if (!isClusterAdmin && !string.Equals(requestedDomain, callerTenant.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            resolved = requestedDomain;
            return true;
        }

        resolved = isClusterAdmin ? "external" : callerTenant.Value;
        return true;
    }
}
