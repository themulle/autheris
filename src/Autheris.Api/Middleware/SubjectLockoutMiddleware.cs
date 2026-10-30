namespace Autheris.Api.Middleware;

using Autheris.Api.Errors;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

public sealed class SubjectLockoutMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SubjectLockoutMiddleware> _logger;

    public SubjectLockoutMiddleware(RequestDelegate next, ILogger<SubjectLockoutMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, IIdentityStoreRepository? identityStore)
    {
        if (context.User?.Identity?.IsAuthenticated == true && identityStore != null)
        {
            var userKey = context.User.FindFirst("oid")?.Value
                ?? context.User.FindFirst("sub")?.Value
                ?? context.User.GetUserSid()?.Value;

            if (!string.IsNullOrWhiteSpace(userKey))
            {
                var realmId = context.User.FindFirst("autheris:realm")?.Value ?? "default";
                var tenantId = context.User.GetTenantId();

                bool isLocked;
                try
                {
                    isLocked = await identityStore.IsSubjectLockedAsync(tenantId, realmId, userKey, context.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Fail closed on store failure (AppSec requirement)
                    _logger.LogError(ex, "Failed to verify subject lockout for user {UserKey} in realm {RealmId}. Failing closed.", userKey, realmId);
                    context.Response.Headers.RetryAfter = "5";
                    await ApiProblems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.ServiceUnavailable, "Identity verification service is temporarily unavailable.", context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                if (isLocked)
                {
                    _logger.LogWarning("Subject {UserKey} in realm {RealmId} is locked. Denying request.", userKey, realmId);
                    await ApiProblems.WriteAsync(context, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "The subject account has been administratively locked or deprovisioned.", context.RequestAborted).ConfigureAwait(false);
                    return;
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
