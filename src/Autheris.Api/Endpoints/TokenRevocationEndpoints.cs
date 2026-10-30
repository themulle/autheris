namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// SEC M-14 (GAP-B): Admin endpoint to revoke a token (<c>jti</c>) or all tokens of a subject issued up to now.
/// Open WebSocket subscriptions of affected tokens are closed at the next revalidation
/// (<c>GraphQL.SubscriptionRevalidationSeconds</c>).
/// </summary>
public static class TokenRevocationEndpoints
{
    internal static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);
    internal static readonly TimeSpan MaxRetention = TimeSpan.FromDays(30);

    public sealed record RevokeTokenRequest(string SubjectOrJti, DateTimeOffset? Until);

    public static IEndpointRouteBuilder MapTokenRevocationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/tokens/revoke", (
            RevokeTokenRequest request,
            HttpContext context,
            ITokenRevocationService revocationService,
            IAuditWriter auditWriter,
            CancellationToken ct) => RevokeAsync(request, context, revocationService, auditWriter, ct))
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Delegated, AuditEventTypes.TokenRevoked);

        return app;
    }

    internal static async Task<IResult> RevokeAsync(
        RevokeTokenRequest request, HttpContext context, ITokenRevocationService revocationService, IAuditWriter auditWriter, CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.SubjectOrJti))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "subjectOrJti is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var until = ResolveUntil(request.Until, now);
        if (until <= now)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "until must be in the future.");
        }

        // Review E-8: only a canonical ClusterAdmin revokes globally. Tenant administrators revoke a tenant-scoped
        // key that matches tokens of their own tenant only and never a canonical ClusterAdmin.
        // In a single-tenant deployment the scope is the legacy tenant, i.e. all non-ClusterAdmin tokens.
        bool isClusterAdmin = EndpointSecurity.IsCanonicalClusterAdmin(context.User);
        TenantId tenantId;
        try
        {
            tenantId = context.User.GetTenantId();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or Autheris.Domain.Exceptions.GatewaySecurityException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        string revocationKey = isClusterAdmin
            ? request.SubjectOrJti
            : TokenRevocationKeys.TenantScoped(tenantId, request.SubjectOrJti);

        await revocationService.RevokeAsync(revocationKey, until, ct).ConfigureAwait(false);

        await auditWriter.RecordAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = AuditEventTypes.TokenRevoked,
            ActorSid = context.User.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "DENY",
            TraceId = context.TraceIdentifier,
            DetailsJson = JsonSerializer.Serialize(new { subjectOrJti = request.SubjectOrJti, until, scope = isClusterAdmin ? "global" : "tenant", tenant = isClusterAdmin ? null : tenantId.Value })
        }, CancellationToken.None).ConfigureAwait(false);   // the revocation is already committed: never take the request token
        context.Features.Get<AuditContext>()?.MarkHandled();

        // Review R4-3: name the tenant scope explicitly. A tenant-scoped revocation only affects tokens that carry this tenant;
        // tokens of the same subject issued under another tenant claim need a ClusterAdmin (global revocation).
        return Results.Ok(new
        {
            revoked = request.SubjectOrJti,
            until,
            scope = isClusterAdmin ? "global" : "tenant",
            tenant = isClusterAdmin ? null : tenantId.Value,
            note = isClusterAdmin ? null : "Applies to tokens of this tenant only. Use a ClusterAdmin to revoke across tenants."
        });
    }

    internal static DateTimeOffset ResolveUntil(DateTimeOffset? requested, DateTimeOffset now)
    {
        var until = requested ?? now + DefaultRetention;
        var max = now + MaxRetention;
        return until > max ? max : until;
    }
}
