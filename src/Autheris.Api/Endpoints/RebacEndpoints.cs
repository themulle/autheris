namespace Autheris.Api.Endpoints;

using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// F-SEC-04: ReBAC REST Endpoints.
/// Exposes tuple management and relationship-based access check APIs with strict multi-tenant boundary checks.
/// </summary>
public static class RebacEndpoints
{
    /// <summary>Review E-10: relationship changes decide access and belong in the audit hash chain.</summary>
    private static async Task AuditTupleChangeAsync(
        IAuditLogRepository auditLog, HttpContext http, TenantId callerTenant, string eventType, string decision, RebacTuple tuple)
    {
        TenantId tenant;
        try
        {
            tenant = new TenantId(tuple.TenantId);
        }
        catch (ArgumentException)
        {
            tenant = callerTenant;
        }

        await auditLog.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = eventType,
            ActorSid = http.User.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = decision,
            TraceId = http.TraceIdentifier,
            DetailsJson = JsonSerializer.Serialize(new { tuple.User, tuple.Relation, tuple.Object, tenant = tuple.TenantId })
        }, http.RequestAborted).ConfigureAwait(false);
    }

    public static IEndpointRouteBuilder MapRebacEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/rebac").RequireAuthorization();

        // POST /api/v1/rebac/tuples - Add relationship tuples
        group.MapPost("/tuples", async (
            List<RebacTuple> tuples,
            HttpRequest request,
            IRebacStore store,
            IRebacEvaluator evaluator,
            IAuditLogRepository auditLog) =>
        {
            var secContext = EndpointSecurity.GetSecurityContext(request.HttpContext);
            if (!secContext.HasAnyRole(GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!secContext.IsClusterAdmin)
            {
                if (secContext.TenantId == TenantId.LegacySingleTenant || string.IsNullOrWhiteSpace(secContext.TenantId.Value))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                foreach (var t in tuples)
                {
                    if (!string.Equals(secContext.TenantId.Value, t.TenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }
            }

            foreach (var t in tuples)
            {
                await store.AddTupleAsync(t, request.HttpContext.RequestAborted).ConfigureAwait(false);
                evaluator.InvalidateTenantCache(t.TenantId);
                await AuditTupleChangeAsync(auditLog, request.HttpContext, secContext.TenantId, "REBAC_TUPLE_ADDED", "ALLOW", t).ConfigureAwait(false);
            }

            return Results.Ok(new { status = "Tuples added", count = tuples.Count });
        });

        // DELETE /api/v1/rebac/tuples - Remove relationship tuple
        group.MapDelete("/tuples", async (
            [FromBody] RebacTuple tuple,
            HttpRequest request,
            IRebacStore store,
            IRebacEvaluator evaluator,
            IAuditLogRepository auditLog) =>
        {
            var secContext = EndpointSecurity.GetSecurityContext(request.HttpContext);
            if (!secContext.HasAnyRole(GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!secContext.IsClusterAdmin)
            {
                if (secContext.TenantId == TenantId.LegacySingleTenant || string.IsNullOrWhiteSpace(secContext.TenantId.Value) ||
                    !string.Equals(secContext.TenantId.Value, tuple.TenantId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }

            var removed = await store.DeleteTupleAsync(tuple, request.HttpContext.RequestAborted).ConfigureAwait(false);
            evaluator.InvalidateTenantCache(tuple.TenantId);
            await AuditTupleChangeAsync(auditLog, request.HttpContext, secContext.TenantId, "REBAC_TUPLE_REMOVED", removed ? "REVOKED" : "NOOP", tuple).ConfigureAwait(false);

            return Results.Ok(new { removed });
        });

        // GET /api/v1/rebac/tuples - Query tuples (restricted to governance admins)
        group.MapGet("/tuples", async (
            HttpRequest request,
            IRebacStore store) =>
        {
            var secContext = EndpointSecurity.GetSecurityContext(request.HttpContext);
            if (!secContext.HasAnyRole(GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var requestedTenant = request.Query["tenantId"].ToString();

            string tenant;
            if (secContext.IsClusterAdmin && !string.IsNullOrWhiteSpace(requestedTenant))
            {
                tenant = requestedTenant;
            }
            else
            {
                if (secContext.TenantId == TenantId.LegacySingleTenant || string.IsNullOrWhiteSpace(secContext.TenantId.Value))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
                tenant = secContext.TenantId.Value;
            }

            var userFilter = request.Query["user"].ToString();
            var relationFilter = request.Query["relation"].ToString();
            var objectFilter = request.Query["object"].ToString();

            var tuples = await store.GetTuplesAsync(
                tenant,
                string.IsNullOrWhiteSpace(userFilter) ? null : userFilter,
                string.IsNullOrWhiteSpace(relationFilter) ? null : relationFilter,
                string.IsNullOrWhiteSpace(objectFilter) ? null : objectFilter,
                request.HttpContext.RequestAborted).ConfigureAwait(false);

            return Results.Ok(tuples);
        });

        // POST /api/v1/rebac/check - Single tuple evaluation
        group.MapPost("/check", async (
            RebacCheckRequest check,
            HttpRequest request,
            IRebacEvaluator evaluator) =>
        {
            var secContext = EndpointSecurity.GetSecurityContext(request.HttpContext);

            if (!secContext.IsClusterAdmin)
            {
                if (secContext.TenantId == TenantId.LegacySingleTenant || string.IsNullOrWhiteSpace(secContext.TenantId.Value) ||
                    !string.Equals(secContext.TenantId.Value, check.TenantId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                // Prevent third-party authorization probing by non-admins
                if (!secContext.HasRole(GatewayRole.GovernanceAdmin))
                {
                    if (!string.Equals(secContext.UserSid.Value, check.User, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }
            }

            var decision = await evaluator.CheckAsync(check, request.HttpContext.RequestAborted).ConfigureAwait(false);
            return Results.Ok(decision);
        });

        // POST /api/v1/rebac/batch-check - Batch evaluation (Zero-N+1, capped at 100 checks)
        group.MapPost("/batch-check", async (
            RebacBatchCheckRequest batchCheck,
            HttpRequest request,
            IRebacEvaluator evaluator) =>
        {
            if (batchCheck.Checks == null || batchCheck.Checks.Count > 100)
            {
                return Results.BadRequest(new { error = "Batch check count cannot exceed 100 items." });
            }

            var secContext = EndpointSecurity.GetSecurityContext(request.HttpContext);

            // Review E-3: every item is evaluated in its own tenant, so each item must belong to the validated batch
            // tenant (otherwise the outer tenant check is meaningless).
            if (batchCheck.Checks.Any(c => c == null || !string.Equals(c.TenantId?.Trim(), batchCheck.TenantId?.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return Results.BadRequest(new { error = "All checks must use the tenant of the batch." });
            }

            if (!secContext.IsClusterAdmin)
            {
                if (secContext.TenantId == TenantId.LegacySingleTenant || string.IsNullOrWhiteSpace(secContext.TenantId.Value) ||
                    !string.Equals(secContext.TenantId.Value, batchCheck.TenantId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                if (!secContext.HasRole(GatewayRole.GovernanceAdmin))
                {
                    foreach (var c in batchCheck.Checks)
                    {
                        if (!string.Equals(secContext.UserSid.Value, c.User, StringComparison.OrdinalIgnoreCase))
                        {
                            return Results.StatusCode(StatusCodes.Status403Forbidden);
                        }
                    }
                }
            }

            var decision = await evaluator.BatchCheckAsync(batchCheck, request.HttpContext.RequestAborted).ConfigureAwait(false);
            return Results.Ok(decision);
        });

        return app;
    }
}
