namespace Autheris.Api.Endpoints;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.State;
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
    public static IEndpointRouteBuilder MapGovernanceApiEndpoints(this IEndpointRouteBuilder app)
    {
        // 2FA Enrollment: generates shared Base32 secret and otpauth:// URI
        app.MapPost("/api/v1/governance/2fa/enroll", async (
            HttpContext context,
            ITotpVerificationService totpService,
            IDistributedClusterStateProvider? clusterState,
            ITotpSecretStore? secretStore) =>
        {
            var userSid = ResolveUserSid(context);
            if (string.IsNullOrWhiteSpace(userSid))
            {
                return Results.Unauthorized();
            }

            var email = ResolveUserEmail(context, userSid);
            var enrollment = totpService.GenerateEnrollment(userSid, email, "Autheris");

            // Cache pending provisional secret with 15 minutes TTL
            var pendingKey = $"totp:pending:{userSid.Trim().ToLowerInvariant()}";
            if (clusterState != null)
            {
                try
                {
                    await clusterState.SetAsync(pendingKey, enrollment.SecretBase32, TimeSpan.FromMinutes(15), context.RequestAborted).ConfigureAwait(false);
                }
                catch
                {
                    // Silently tolerate store issues
                }
            }

            return Results.Ok(enrollment);
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRequested);

        // GET convenience alias for enrollment
        app.MapGet("/api/v1/governance/2fa/enroll", async (
            HttpContext context,
            ITotpVerificationService totpService,
            IDistributedClusterStateProvider? clusterState,
            ITotpSecretStore? secretStore) =>
        {
            var userSid = ResolveUserSid(context);
            if (string.IsNullOrWhiteSpace(userSid))
            {
                return Results.Unauthorized();
            }

            var email = ResolveUserEmail(context, userSid);
            var enrollment = totpService.GenerateEnrollment(userSid, email, "Autheris");

            var pendingKey = $"totp:pending:{userSid.Trim().ToLowerInvariant()}";
            if (clusterState != null)
            {
                try
                {
                    await clusterState.SetAsync(pendingKey, enrollment.SecretBase32, TimeSpan.FromMinutes(15), context.RequestAborted).ConfigureAwait(false);
                }
                catch
                {
                    // Silently tolerate
                }
            }

            return Results.Ok(enrollment);
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRequested);

        // 2FA Activation: verifies initial code from authenticator app and activates secret
        app.MapPost("/api/v1/governance/2fa/verify-enrollment", async (
            TotpVerifyRequest request,
            HttpContext context,
            ITotpVerificationService totpService,
            ITotpSecretStore secretStore,
            IDistributedClusterStateProvider? clusterState) =>
        {
            var userSid = ResolveUserSid(context);
            if (string.IsNullOrWhiteSpace(userSid))
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request?.TotpCode))
            {
                return Results.BadRequest(new TotpVerifyResult(false, "TotpCode is required."));
            }

            string? secretBase32 = request.SecretBase32;
            if (string.IsNullOrWhiteSpace(secretBase32) && clusterState != null)
            {
                try
                {
                    var pendingKey = $"totp:pending:{userSid.Trim().ToLowerInvariant()}";
                    secretBase32 = await clusterState.GetAsync<string>(pendingKey, context.RequestAborted).ConfigureAwait(false);
                }
                catch
                {
                    // Silently tolerate
                }
            }

            if (string.IsNullOrWhiteSpace(secretBase32))
            {
                return Results.BadRequest(new TotpVerifyResult(false, "No pending enrollment found. Please call /enroll first or supply SecretBase32."));
            }

            var isValid = await totpService.VerifyAndConsumeTotpAsync(userSid, secretBase32, request.TotpCode, context.RequestAborted).ConfigureAwait(false);
            if (!isValid)
            {
                return Results.BadRequest(new TotpVerifyResult(false, "Invalid or expired TOTP code."));
            }

            // Persist verified secret in secret store for subsequent step-up validations
            await secretStore.SetSecretAsync(userSid, secretBase32, context.RequestAborted).ConfigureAwait(false);

            if (clusterState != null)
            {
                try
                {
                    var pendingKey = $"totp:pending:{userSid.Trim().ToLowerInvariant()}";
                    await clusterState.RemoveAsync(pendingKey, context.RequestAborted).ConfigureAwait(false);
                }
                catch
                {
                    // Silently tolerate
                }
            }

            return Results.Ok(new TotpVerifyResult(true));
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
