namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.UI;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public static class DevPortalEndpoints
{
    public static IEndpointRouteBuilder MapDevPortalEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        IResult HandleDevPortal(HttpContext context, IWebHostEnvironment env)
        {
            // SEC-T2 Hardening: Strictly forbidden outside of Development environment (Fail-Closed)
            if (!env.IsDevelopment())
            {
                return Results.NotFound();
            }

            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            SetPortalSecurityHeaders(context, nonce, noStore: false);

            var html = DevPortalAssets.GenerateDevPortalHtml(gatewayOptions, env.EnvironmentName, nonce);
            return Results.Content(html, "text/html;charset=utf-8");
        }

        app.MapGet("/", HandleDevPortal).AllowAnonymous().WithAuditExemption("Root gateway landing/discovery page");
        app.MapGet("/getting-started", HandleDevPortal).AllowAnonymous().WithAuditExemption("Developer getting started guide");

        // R-60 / R-64: DevPortal 2FA Enrollment Web Console
        app.MapGet("/portal/2fa/enroll", async (
            HttpContext context,
            ITotpEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            SetPortalSecurityHeaders(context, nonce, noStore: true);

            var userSid = context.User.GetUserSid()?.Value
                          ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? "developer";

            // Security review (TOTP TOFU): a first enrollment requires a fresh interactive sign-in (not in Development).
            var firstEnrollment = await TotpFirstEnrollmentGuard.CheckAsync(context, enrollmentService, userSid, "begin", ct).ConfigureAwait(false);
            if (firstEnrollment.Rejection != null)
            {
                return firstEnrollment.Rejection;
            }

            // SR-M-01: the seed is generated and kept server-side (pending, bound to the user); the page never sends it back.
            var enrollment = await enrollmentService.BeginEnrollmentAsync(userSid, $"{userSid}@autheris.local", "Autheris Gateway", ct).ConfigureAwait(false);
            if (enrollment == null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var requiresCurrentCode = !firstEnrollment.IsFirstEnrollment;
            var svg = SvgQrCodeGenerator.GenerateSvg(enrollment.QrCodeUri);

            var formattedSecret = string.Join(" ", Enumerable.Range(0, Math.Max(1, enrollment.SecretBase32.Length / 4))
                .Select(i => enrollment.SecretBase32.Substring(i * 4, Math.Min(4, enrollment.SecretBase32.Length - i * 4))));

            var html = DevPortalAssets.Generate2FaEnrollHtml(userSid, formattedSecret, svg, nonce, requiresCurrentCode);
            return Results.Content(html, "text/html;charset=utf-8");
        }).WithAudit(AuditLevel.Full, AuditEventTypes.TwoFactorEnrollmentStarted);

        app.MapPost("/portal/2fa/enroll/verify", async (
            HttpContext context,
            ITotpEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            string code = string.Empty;
            string currentCode = string.Empty;

            // SR-M-01: a client supplied "secret" is deliberately ignored; only the server-side pending seed is verified.
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(ct);
                code = form["totpCode"].ToString();
                currentCode = form["currentTotpCode"].ToString();
            }
            else
            {
                try
                {
                    var body = await context.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
                    code = body.TryGetProperty("totpCode", out var c) ? c.GetString() ?? "" : "";
                    currentCode = body.TryGetProperty("currentTotpCode", out var cc) ? cc.GetString() ?? "" : "";
                }
                catch
                {
                    // Ignore JSON parse errors
                }
            }

            var userSid = context.User.GetUserSid()?.Value
                          ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? "developer";

            if (string.IsNullOrWhiteSpace(code))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Code is required.");
            }

            // Security review (TOTP TOFU): checked again on activation, the pending seed alone does not prove a fresh sign-in.
            // The verify window covers the pending-seed lifetime, so an enrollment admitted at "begin" is not re-failed here.
            var firstEnrollment = await TotpFirstEnrollmentGuard.CheckAsync(context, enrollmentService, userSid, "verify", ct).ConfigureAwait(false);
            if (firstEnrollment.Rejection != null)
            {
                return firstEnrollment.Rejection;
            }

            var outcome = await enrollmentService.CompleteEnrollmentAsync(
                userSid, code, string.IsNullOrWhiteSpace(currentCode) ? null : currentCode, ct).ConfigureAwait(false);

            if (outcome == TotpEnrollmentOutcome.Completed && firstEnrollment.IsFirstEnrollment)
            {
                // Required audit (fail closed): an unavailable audit store aborts with 503 via the gateway exception handler.
                await TotpFirstEnrollmentGuard.RecordFirstEnrollmentCompletedAsync(context, userSid, ct).ConfigureAwait(false);
            }

            return outcome switch
            {
                TotpEnrollmentOutcome.Completed => Results.Ok(new { success = true, message = "2FA Enrollment completed successfully." }),
                TotpEnrollmentOutcome.NoPendingEnrollment => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "No pending enrollment found. Reload the enrollment page first."),
                TotpEnrollmentOutcome.CurrentCodeRequired => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "A code from the currently enrolled authenticator is required to replace the 2FA secret."),
                TotpEnrollmentOutcome.InvalidCurrentCode => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Invalid or expired code for the current authenticator."),
                TotpEnrollmentOutcome.StoreUnavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                _ => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Invalid or expired TOTP code.")
            };
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.TwoFactorEnrollmentVerified);

        // R-64 UX: DevPortal HitL Step-Up Approvals Console
        app.MapGet("/portal/approvals", (
            HttpContext context,
            IHitLStepUpApprovalService hitlService,
            IOptions<GatewayOptions> options) =>
        {
            if (!options.Value.HitLStepUp.Enabled)
            {
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "HitL step-up approval is disabled.");
            }

            if (!EndpointSecurity.IsApprover(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            SetPortalSecurityHeaders(context, nonce, noStore: true);

            var tenant = EndpointSecurity.GetRequestTenant(context);
            var tickets = hitlService.GetPendingTickets(tenant.Value);

            var html = DevPortalAssets.GenerateApprovalsHtml(tenant.Value, tickets, nonce);
            return Results.Content(html, "text/html;charset=utf-8");
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.StepUpApprovalsListed);

        app.MapPost("/portal/approvals/{ticketId}/step-up", async (
            string ticketId,
            HttpContext context,
            IHitLStepUpApprovalService hitlService,
            IOptions<GatewayOptions> options,
            CancellationToken ct) =>
        {
            if (!options.Value.HitLStepUp.Enabled)
            {
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "HitL step-up approval is disabled.");
            }

            if (!EndpointSecurity.IsApprover(context.User) || string.IsNullOrWhiteSpace(ticketId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            string totpCode = string.Empty;
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(ct);
                totpCode = form["totpCode"].ToString();
            }
            else
            {
                try
                {
                    var body = await context.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
                    totpCode = body.TryGetProperty("totpCode", out var c) ? c.GetString() ?? "" : "";
                }
                catch
                {
                    // Ignore
                }
            }

            var approver = HitLEndpoints.BuildApproverContext(context);
            if (approver == null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var forbidden = await HitLEndpoints.EnsureTableApproverAsync(context, hitlService, ticketId, approver).ConfigureAwait(false);
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await hitlService.ApproveStepUpRequestAsync(ticketId, approver, totpCode, ct);
            if (!result.IsApproved)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Step-up approval failed.");
            }

            return Results.Ok(new { success = true, confirmationToken = result.Ticket?.Signature ?? result.Ticket?.ApprovalId ?? ticketId });
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.StepUpApprovalAttempted);

        return app;
    }

    private static void SetPortalSecurityHeaders(HttpContext context, string nonce, bool noStore = false)
    {
        context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        if (noStore)
        {
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, private";
            context.Response.Headers["Pragma"] = "no-cache";
        }
    }
}
