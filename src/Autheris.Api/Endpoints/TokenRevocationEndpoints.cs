namespace Autheris.Api.Endpoints;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
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
        app.MapPost("/api/admin/tokens/revoke", async (
            RevokeTokenRequest request,
            HttpContext context,
            ITokenRevocationService revocationService,
            IAuditLogRepository auditLogRepository,
            CancellationToken ct) =>
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SubjectOrJti))
            {
                return Results.BadRequest(new { error = "subjectOrJti is required." });
            }

            var now = DateTimeOffset.UtcNow;
            var until = ResolveUntil(request.Until, now);
            if (until <= now)
            {
                return Results.BadRequest(new { error = "until must be in the future." });
            }

            await revocationService.RevokeAsync(request.SubjectOrJti, until, ct).ConfigureAwait(false);

            var tenantId = TenantId.TryParse(context.User.FindFirst("tenant_id")?.Value, out var tid) ? tid : TenantId.LegacySingleTenant;
            await auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "TOKEN_REVOKED",
                ActorSid = context.User.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
                TargetTable = string.Empty,
                Decision = "DENY",
                TraceId = context.TraceIdentifier,
                DetailsJson = JsonSerializer.Serialize(new { subjectOrJti = request.SubjectOrJti, until })
            }, ct).ConfigureAwait(false);

            return Results.Ok(new { revoked = request.SubjectOrJti, until });
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin);

        return app;
    }

    internal static DateTimeOffset ResolveUntil(DateTimeOffset? requested, DateTimeOffset now)
    {
        var until = requested ?? now + DefaultRetention;
        var max = now + MaxRetention;
        return until > max ? max : until;
    }
}
