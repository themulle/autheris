namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.SchemaRegistry;
using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class SchemaRegistryEndpoints
{
    private const long MaxSchemaPayloadBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSchemaRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        // Schema Registry & CI/CD Endpoints (P8)
        app.MapPost("/api/schema-registry/publish", async (
            HttpContext httpContext,
            ISchemaRegistryService registry,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!GatewayPolicies.HasAnyRole(principal, GatewayPolicies.SchemaPublisherRoles))
            {
                return Results.Json(new { error = "Publishing schemas requires Developer, SchemaAdmin, or ClusterAdmin privileges." }, statusCode: StatusCodes.Status403Forbidden);
            }

            // SEC M-07: Bounded read instead of a bypassable Content-Length check (model binding would read unbounded).
            var (request, error) = await ReadRegistrationRequestAsync(
                httpContext.Request,
                "Schema registration payload exceeds maximum allowed size (10 MB).",
                ct);
            if (request == null)
            {
                return error!;
            }

            // SR-M-11: registry keys are tenant-scoped; the client-visible service name stays unscoped.
            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var publicName = request.ServiceName;
            request = WithServiceName(request, ScopeServiceName(tenantId, publicName));

            var isSchemaAdmin = GatewayPolicies.HasAnyRole(principal, GatewayPolicies.SchemaAdminRoles);
            if (request.ForceIfBreaking && !isSchemaAdmin)
            {
                return Results.Json(new { error = "ForceIfBreaking requires administrative privileges (GovernanceAdmin, SchemaAdmin, or ClusterAdmin)." }, statusCode: StatusCodes.Status403Forbidden);
            }

            // SEC M-12: RegisteredBy is set server-side from the authenticated principal; the body value is ignored.
            var callerId = EndpointSecurity.GetCallerIdentity(httpContext);
            if (string.IsNullOrWhiteSpace(callerId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // SEC M-12: Non-admin publishers may only publish for services they own (owner = first registrant).
            if (!isSchemaAdmin && !request.DryRun)
            {
                var history = await registry.GetSchemaHistoryAsync(request.ServiceName, ct);
                if (!IsServiceOwner(history, callerId))
                {
                    return Results.Json(new { error = $"Only the owner of service '{publicName}' or a SchemaAdmin may publish new schema versions." }, statusCode: StatusCodes.Status403Forbidden);
                }
            }

            var serverSideRequest = WithRegisteredBy(request, callerId);
            var response = await registry.RegisterSchemaAsync(serverSideRequest, ct);
            var publicResponse = new SchemaRegistrationResponse
            {
                Success = response.Success,
                Message = response.Message,
                Schema = response.Schema == null ? null : UnscopeSchema(response.Schema, tenantId),
                Diff = response.Diff
            };
            return response.Success
                ? Results.Ok(publicResponse)
                : Results.BadRequest(publicResponse);
        }).RequireAuthorization()
          .WithRequestBodyLimit(MaxSchemaPayloadBytes) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapPost("/api/schema-registry/check", async (
            HttpContext httpContext,
            ISchemaRegistryService registry,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!GatewayPolicies.HasAnyRole(principal, GatewayPolicies.SchemaPublisherRoles))
            {
                return Results.Json(new { error = "Checking schemas requires Developer, SchemaAdmin, or ClusterAdmin privileges." }, statusCode: StatusCodes.Status403Forbidden);
            }

            // SEC M-07: Bounded read instead of a bypassable Content-Length check.
            var (request, error) = await ReadRegistrationRequestAsync(
                httpContext.Request,
                "Schema check payload exceeds maximum allowed size (10 MB).",
                ct);
            if (request == null)
            {
                return error!;
            }

            var diff = await registry.CheckSchemaAsync(ScopeServiceName(EndpointSecurity.GetRequestTenant(httpContext), request.ServiceName), request.Sdl, ct);
            return Results.Ok(new
            {
                serviceName = request.ServiceName,
                isCompatible = diff.IsCompatible,
                hasBreakingChanges = diff.HasBreakingChanges,
                breakingCount = diff.BreakingCount,
                dangerousCount = diff.DangerousCount,
                safeCount = diff.SafeCount,
                changes = diff.Changes
            });
        }).RequireAuthorization()
          .WithRequestBodyLimit(MaxSchemaPayloadBytes) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/api/schema-registry/{service}/latest", async (
            string service,
            HttpContext httpContext,
            ISchemaRegistryService registry,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!GatewayPolicies.HasAnyRole(principal, GatewayPolicies.SchemaPublisherRoles))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var latest = await registry.GetLatestSchemaAsync(ScopeServiceName(tenantId, service), ct);
            return latest != null ? Results.Ok(UnscopeSchema(latest, tenantId)) : Results.NotFound(new { error = $"No active schema found for service '{service}'." });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/api/schema-registry/{service}/history", async (
            string service,
            HttpContext httpContext,
            ISchemaRegistryService registry,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!GatewayPolicies.HasAnyRole(principal, GatewayPolicies.SchemaPublisherRoles))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var history = await registry.GetSchemaHistoryAsync(ScopeServiceName(tenantId, service), ct);
            return Results.Ok(history.Select(h => UnscopeSchema(h, tenantId)).ToList());
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/api/schema-registry/services", async (
            HttpContext httpContext,
            ISchemaRegistryService registry,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!GatewayPolicies.HasAnyRole(principal, GatewayPolicies.SchemaPublisherRoles))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var all = await registry.GetAllServicesAsync(ct);
            if (EndpointSecurity.IsClusterAdmin(principal))
            {
                // Cluster admin sees every tenant's services (keys keep their tenant prefix).
                return Results.Ok(all);
            }

            var prefix = ScopePrefix(EndpointSecurity.GetRequestTenant(httpContext));
            return Results.Ok(all
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                .Select(n => n[prefix.Length..])
                .ToList());
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        return app;
    }

    /// <summary>
    /// SR-M-11: Fixed-length (16 hex chars) tenant prefix so that scoped keys stay valid service names and
    /// cannot collide across tenants (the prefix length is constant, so tenant/service boundaries are unambiguous).
    /// </summary>
    internal static string ScopePrefix(Autheris.Domain.Common.TenantId tenantId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(tenantId.Value.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + "_";
    }

    internal static string ScopeServiceName(Autheris.Domain.Common.TenantId tenantId, string serviceName)
        => ScopePrefix(tenantId) + serviceName;

    internal static RegisteredSchema UnscopeSchema(RegisteredSchema schema, Autheris.Domain.Common.TenantId tenantId)
    {
        var prefix = ScopePrefix(tenantId);
        return new RegisteredSchema
        {
            ServiceName = schema.ServiceName.StartsWith(prefix, StringComparison.Ordinal) ? schema.ServiceName[prefix.Length..] : schema.ServiceName,
            Version = schema.Version,
            Sdl = schema.Sdl,
            RegisteredAt = schema.RegisteredAt,
            GitCommit = schema.GitCommit,
            GitBranch = schema.GitBranch,
            RegisteredBy = schema.RegisteredBy,
            IsActive = schema.IsActive
        };
    }

    private static SchemaRegistrationRequest WithServiceName(SchemaRegistrationRequest request, string serviceName)
        => new()
        {
            ServiceName = serviceName,
            Sdl = request.Sdl,
            Version = request.Version,
            GitCommit = request.GitCommit,
            GitBranch = request.GitBranch,
            RegisteredBy = request.RegisteredBy,
            DryRun = request.DryRun,
            ForceIfBreaking = request.ForceIfBreaking
        };

    /// <summary>
    /// SEC M-12: Ownership without a dedicated owner field: the identity that registered the first (oldest) version of a
    /// service is its owner. A service without history can be claimed by any publisher (first registrant becomes owner).
    /// </summary>
    internal static bool IsServiceOwner(IReadOnlyList<RegisteredSchema> history, string? callerId)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(callerId))
        {
            return false;
        }

        var owner = history.OrderBy(h => h.RegisteredAt).First().RegisteredBy;
        return !string.IsNullOrWhiteSpace(owner) &&
               string.Equals(owner.Trim(), callerId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SEC M-12: Copy of the request with the server-determined <see cref="SchemaRegistrationRequest.RegisteredBy"/>.
    /// </summary>
    internal static SchemaRegistrationRequest WithRegisteredBy(SchemaRegistrationRequest request, string registeredBy)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new SchemaRegistrationRequest
        {
            ServiceName = request.ServiceName,
            Sdl = request.Sdl,
            Version = request.Version,
            GitCommit = request.GitCommit,
            GitBranch = request.GitBranch,
            RegisteredBy = registeredBy,
            DryRun = request.DryRun,
            ForceIfBreaking = request.ForceIfBreaking
        };
    }

    private static async Task<(SchemaRegistrationRequest? Request, IResult? Error)> ReadRegistrationRequestAsync(
        HttpRequest httpRequest,
        string tooLargeMessage,
        CancellationToken ct)
    {
        var (body, tooLarge) = await EndpointSecurity.TryReadBodyAsync(httpRequest, MaxSchemaPayloadBytes, tooLargeMessage, ct);
        if (body == null)
        {
            return (null, tooLarge);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, Results.BadRequest(new { error = "Empty schema registration payload." }));
        }

        try
        {
            var request = JsonSerializer.Deserialize<SchemaRegistrationRequest>(body, RequestJsonOptions);
            if (request == null || string.IsNullOrWhiteSpace(request.ServiceName) || request.Sdl == null)
            {
                return (null, Results.BadRequest(new { error = "Invalid schema registration payload." }));
            }

            return (request, null);
        }
        catch (JsonException)
        {
            return (null, Results.BadRequest(new { error = "Invalid schema registration payload." }));
        }
    }
}
