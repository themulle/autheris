namespace Autheris.Api.Endpoints;

using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Audit;
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
        IAuditWriter auditWriter, HttpContext http, TenantId callerTenant, string eventType, string decision, RebacTuple tuple)
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

        await auditWriter.RecordAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = eventType,
            ActorSid = http.User.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = decision,
            TraceId = http.TraceIdentifier,
            DetailsJson = JsonSerializer.Serialize(new { tuple.User, tuple.Relation, tuple.Object, tenant = tuple.TenantId })
        }, CancellationToken.None).ConfigureAwait(false);   // post-commit: a client disconnect must not drop the record
    }

    internal static async Task AuditInvalidationFailureAsync(
        IAuditWriter auditWriter, HttpContext http, TenantId callerTenant, string tenantId, Exception ex)
    {
        TenantId tenant;
        try
        {
            tenant = new TenantId(tenantId);
        }
        catch (ArgumentException)
        {
            tenant = callerTenant;
        }

        await auditWriter.RecordAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = AuditEventTypes.RebacInvalidationFailed,
            ActorSid = http.User.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "DENY",
            TraceId = http.TraceIdentifier,
            DetailsJson = JsonSerializer.Serialize(new { reasonType = ex.GetType().Name, tenant = tenantId })
        }, CancellationToken.None).ConfigureAwait(false);   // post-commit: a client disconnect must not drop the record
    }

    /// <summary>Adds tuples. The audit is fail-closed (an unavailable store surfaces as 503 through the exception handler) and non-cancelable.</summary>
    internal static async Task<IResult> AddTuplesAsync(
        List<RebacTuple> tuples, HttpRequest request, IRebacStore store, IRebacEvaluator evaluator, IAuditWriter auditWriter, IGrantPolicy? grantPolicy = null)
    {
        var secContext = EndpointSecurity.GetSecurityContext(request.HttpContext);
        if (!secContext.HasAnyRole(GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // POL-7: Reject empty tuple fields (User, Relation, Object, TenantId, Namespace)
        foreach (var t in tuples)
        {
            if (t == null ||
                string.IsNullOrWhiteSpace(t.User) ||
                string.IsNullOrWhiteSpace(t.Relation) ||
                string.IsNullOrWhiteSpace(t.Object) ||
                string.IsNullOrWhiteSpace(t.TenantId) ||
                (t.Object.Contains(':') && (string.IsNullOrWhiteSpace(t.Object.Split(':')[0]) || string.IsNullOrWhiteSpace(t.Object.Split(':')[1]))))
            {
                return Results.BadRequest(new { error = "ReBAC tuple fields (User, Relation, Object, TenantId) must not be empty." });
            }
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

        // EP-08: Validate and canonicalize subjects before evaluating policy or storing.
        var canonicalTuples = new List<RebacTuple>(tuples.Count);
        foreach (var t in tuples)
        {
            var parsed = RebacSubject.Parse(t.User);
            if (!parsed.IsValid)
            {
                return Results.BadRequest(new { error = $"Invalid ReBAC subject: '{t.User}'.", errorCode = "INVALID_REBAC_SUBJECT" });
            }
            canonicalTuples.Add(t with { User = parsed.Canonical });
        }

        // WP-SC-16: a tuple for an individual user is a time-bound individual grant (explicit ExpiresAt within the class maximum).
        // Usersets (group#member) and structural tuples (parent) are standing and not limited.
        var policy = grantPolicy ?? new GrantPolicy();
        var now = DateTimeOffset.UtcNow;
        var requester = request.HttpContext.User.GetUserSid()?.Value;
        foreach (var t in canonicalTuples)
        {
            var parsed = RebacSubject.Parse(t.User);
            if (parsed.Kind != RebacSubjectKind.Individual)
            {
                continue;
            }

            var decision = policy.Evaluate(new GrantRequest(
                GranteeType.User, t.User, GrantKind.RebacTuple, t.ExpiresAt, now, Requester: requester));
            if (!decision.Allowed)
            {
                return Results.BadRequest(new { error = decision.Reason, errorCode = decision.ErrorCode, maxValidTo = decision.MaxValidTo });
            }
        }

        foreach (var t in canonicalTuples)
        {
            await store.AddTupleAsync(t, request.HttpContext.RequestAborted).ConfigureAwait(false);
            try
            {
                await evaluator.InvalidateTenantCacheAsync(t.TenantId, request.HttpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await AuditInvalidationFailureAsync(auditWriter, request.HttpContext, secContext.TenantId, t.TenantId, ex).ConfigureAwait(false);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            await AuditTupleChangeAsync(auditWriter, request.HttpContext, secContext.TenantId, AuditEventTypes.RebacTupleAdded, "ALLOW", t).ConfigureAwait(false);
        }

        return Results.Ok(new { status = "Tuples added", count = canonicalTuples.Count });
    }

    internal static async Task<IResult> RemoveTupleAsync(
        RebacTuple tuple, HttpRequest request, IRebacStore store, IRebacEvaluator evaluator, IAuditWriter auditWriter)
    {
        var secContext = EndpointSecurity.GetSecurityContext(request.HttpContext);
        if (!secContext.HasAnyRole(GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // POL-7: Reject empty tuple fields (User, Relation, Object, TenantId, Namespace)
        if (tuple == null ||
            string.IsNullOrWhiteSpace(tuple.User) ||
            string.IsNullOrWhiteSpace(tuple.Relation) ||
            string.IsNullOrWhiteSpace(tuple.Object) ||
            string.IsNullOrWhiteSpace(tuple.TenantId) ||
            (tuple.Object.Contains(':') && (string.IsNullOrWhiteSpace(tuple.Object.Split(':')[0]) || string.IsNullOrWhiteSpace(tuple.Object.Split(':')[1]))))
        {
            return Results.BadRequest(new { error = "ReBAC tuple fields (User, Relation, Object, TenantId) must not be empty." });
        }

        var parsed = RebacSubject.Parse(tuple.User);
        if (!parsed.IsValid)
        {
            return Results.BadRequest(new { error = $"Invalid ReBAC subject: '{tuple.User}'.", errorCode = "INVALID_REBAC_SUBJECT" });
        }
        var canonicalTuple = tuple with { User = parsed.Canonical };

        if (!secContext.IsClusterAdmin)
        {
            if (secContext.TenantId == TenantId.LegacySingleTenant || string.IsNullOrWhiteSpace(secContext.TenantId.Value) ||
                !string.Equals(secContext.TenantId.Value, canonicalTuple.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        var removed = await store.DeleteTupleAsync(canonicalTuple, request.HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            await evaluator.InvalidateTenantCacheAsync(canonicalTuple.TenantId, request.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AuditInvalidationFailureAsync(auditWriter, request.HttpContext, secContext.TenantId, canonicalTuple.TenantId, ex).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        await AuditTupleChangeAsync(auditWriter, request.HttpContext, secContext.TenantId, AuditEventTypes.RebacTupleRemoved, removed ? "REVOKED" : "NOOP", canonicalTuple).ConfigureAwait(false);

        return Results.Ok(new { removed });
    }

    public static IEndpointRouteBuilder MapRebacEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/rebac")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // POST /api/v1/rebac/tuples - Add relationship tuples
        group.MapPost("/tuples", (
            List<RebacTuple> tuples,
            HttpRequest request,
            IRebacStore store,
            IRebacEvaluator evaluator,
            IAuditWriter auditWriter,
            IGrantPolicy? grantPolicy) => AddTuplesAsync(tuples, request, store, evaluator, auditWriter, grantPolicy));

        // DELETE /api/v1/rebac/tuples - Remove relationship tuple
        group.MapDelete("/tuples", (
            [FromBody] RebacTuple tuple,
            HttpRequest request,
            IRebacStore store,
            IRebacEvaluator evaluator,
            IAuditWriter auditWriter) => RemoveTupleAsync(tuple, request, store, evaluator, auditWriter));

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
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // POST /api/v1/rebac/check - Single tuple evaluation
        group.MapPost("/check", async (
            RebacCheckRequest check,
            HttpRequest request,
            IRebacEvaluator evaluator) =>
        {
            // POL-7: Reject empty check fields
            if (check == null ||
                string.IsNullOrWhiteSpace(check.User) ||
                string.IsNullOrWhiteSpace(check.Relation) ||
                string.IsNullOrWhiteSpace(check.Object) ||
                string.IsNullOrWhiteSpace(check.TenantId))
            {
                return Results.BadRequest(new { error = "ReBAC check fields (User, Relation, Object, TenantId) must not be empty." });
            }

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
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuthSucceeded);

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

            // POL-7: Reject empty fields in batch check
            if (string.IsNullOrWhiteSpace(batchCheck.TenantId) ||
                batchCheck.Checks.Any(c => c == null ||
                    string.IsNullOrWhiteSpace(c.User) ||
                    string.IsNullOrWhiteSpace(c.Relation) ||
                    string.IsNullOrWhiteSpace(c.Object) ||
                    string.IsNullOrWhiteSpace(c.TenantId)))
            {
                return Results.BadRequest(new { error = "ReBAC batch check items must not contain empty fields." });
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
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuthSucceeded);

        return app;
    }
}
