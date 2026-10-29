namespace Autheris.Api.Endpoints;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Control plane governance endpoints for Two-Factor Authentication (RFC 6238 TOTP).
/// Enables standard authenticator apps (Microsoft Authenticator, Google Authenticator, 1Password)
/// to enroll and verify second factor credentials for step-up approvals (ADR-05).
/// </summary>
public static class GovernanceApiEndpoints
{
    internal static void ApplyNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    public static IEndpointRouteBuilder MapGovernanceApiEndpoints(this IEndpointRouteBuilder app)
    {
        // 2FA Enrollment: generates a shared Base32 secret server-side and keeps it as a pending, user-bound seed.
        // Re-enrollment of an already enrolled user is only completed with a valid code from the current secret (SR-M-01).
        async Task<IResult> BeginEnrollment(HttpContext context, ITotpEnrollmentService enrollmentService)
        {
            // EP-12: the response carries the TOTP seed; it must never be cached (POST only, no GET alias).
            ApplyNoStore(context.Response);

            var userSid = ResolveUserSid(context);
            if (string.IsNullOrWhiteSpace(userSid))
            {
                return Results.Unauthorized();
            }

            // Security review (TOTP TOFU): a first enrollment requires a fresh interactive sign-in (not in Development).
            var firstEnrollment = await TotpFirstEnrollmentGuard.CheckAsync(context, enrollmentService, userSid, "begin", context.RequestAborted).ConfigureAwait(false);
            if (firstEnrollment.Rejection != null)
            {
                return firstEnrollment.Rejection;
            }

            var email = ResolveUserEmail(context, userSid);
            var enrollment = await enrollmentService.BeginEnrollmentAsync(userSid, email, "Autheris", context.RequestAborted).ConfigureAwait(false);
            if (enrollment == null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(enrollment);
        }

        app.MapPost("/api/v1/governance/2fa/enroll", BeginEnrollment)
            .RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRequested);

        // 2FA Activation: verifies the initial code against the server-side pending seed and activates it
        app.MapPost("/api/v1/governance/2fa/verify-enrollment", async (
            TotpVerifyRequest request,
            HttpContext context,
            ITotpEnrollmentService enrollmentService) =>
        {
            var userSid = ResolveUserSid(context);
            if (string.IsNullOrWhiteSpace(userSid))
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request?.TotpCode))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.TotpCodeRequired, "A TOTP code is required.", context);
            }

            // Security review (TOTP TOFU): checked again on activation; the verify window covers the pending-seed lifetime.
            var firstEnrollment = await TotpFirstEnrollmentGuard.CheckAsync(context, enrollmentService, userSid, "verify", context.RequestAborted).ConfigureAwait(false);
            if (firstEnrollment.Rejection != null)
            {
                return firstEnrollment.Rejection;
            }

            var outcome = await enrollmentService.CompleteEnrollmentAsync(
                userSid, request.TotpCode, request.CurrentTotpCode, context.RequestAborted).ConfigureAwait(false);

            if (outcome == TotpEnrollmentOutcome.Completed && firstEnrollment.IsFirstEnrollment)
            {
                // Required audit (fail closed): an unavailable audit store aborts with 503 via the gateway exception handler.
                await TotpFirstEnrollmentGuard.RecordFirstEnrollmentCompletedAsync(context, userSid, context.RequestAborted).ConfigureAwait(false);
            }

            return outcome switch
            {
                TotpEnrollmentOutcome.Completed => Results.Ok(new TotpVerifyResult(true)),
                TotpEnrollmentOutcome.NoPendingEnrollment => ApiProblems.BadRequest(ApiErrorCodes.TotpNoPendingEnrollment, "No pending enrollment found. Call the enroll endpoint first.", context),
                TotpEnrollmentOutcome.CurrentCodeRequired => ApiProblems.BadRequest(ApiErrorCodes.TotpCurrentCodeRequired, "The current TOTP code is required to replace an active second factor.", context),
                TotpEnrollmentOutcome.InvalidCurrentCode => ApiProblems.BadRequest(ApiErrorCodes.TotpCodeInvalid, "The code is invalid or expired.", context),
                TotpEnrollmentOutcome.StoreUnavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                _ => ApiProblems.BadRequest(ApiErrorCodes.TotpCodeInvalid, "The code is invalid or expired.", context)
            };
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        return app;
    }

    private static string? ResolveUserSid(HttpContext context)
    {
        var securityContext = EndpointSecurity.GetSecurityContext(context);
        return securityContext != null ? securityContext.UserSid.Value : context.User.GetUserSid()?.Value;
    }

    private static string ResolveUserEmail(HttpContext context, string fallbackUserSid)
    {
        var email = context.User.FindFirst(ClaimTypes.Email)?.Value
                 ?? context.User.FindFirst("email")?.Value
                 ?? context.User.FindFirst("upn")?.Value;

        return !string.IsNullOrWhiteSpace(email) ? email.Trim() : $"{fallbackUserSid}@autheris.local";
    }
}
