namespace Autheris.Api.Endpoints;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal sealed class ScimEndpointGuardMetadata
{
    internal static readonly ScimEndpointGuardMetadata Instance = new();

    private ScimEndpointGuardMetadata()
    {
    }
}

public static class ScimEndpointGuard
{
    public const long MaxRequestBodyBytes = 1_048_576;

    internal static RouteGroupBuilder ApplyScimGuard(this RouteGroupBuilder group) =>
        group.WithMetadata(ScimEndpointGuardMetadata.Instance);

    internal static async Task MiddlewareAsync(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ScimEndpointGuardMetadata>() is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var rejected = await EnforceBeforeExecutionAsync(context, context.RequestAborted).ConfigureAwait(false);
        if (rejected is not null)
        {
            await rejected.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await ScimErrorResult.TooMany("SCIM request payload exceeds the 1 MiB limit.")
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        }
        catch (AuditUnavailableException)
        {
            await ScimErrorResult.AuditUnavailable()
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        }
    }

    public static async Task<IResult?> EnforceBeforeExecutionAsync(HttpContext httpContext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (IsWriteMethod(httpContext.Request.Method) && httpContext.Request.ContentLength is > MaxRequestBodyBytes)
        {
            return ScimErrorResult.TooMany("SCIM request payload exceeds the 1 MiB limit.");
        }

        var realmId = ResolveRealmId(httpContext);
        var rateLimiter = httpContext.RequestServices?.GetService<IRateLimiterService>();
        if (rateLimiter is not null && !string.IsNullOrWhiteSpace(realmId))
        {
            var realmOptions = httpContext.RequestServices?.GetService<IOptionsMonitor<IdentityRealmsOptions>>()?.CurrentValue;
            var scimOptions = realmOptions?.Scim ?? new ScimOptions();
            IdentityRealmConfigOptions? realmConfig = null;
            realmOptions?.Realms.TryGetValue(realmId, out realmConfig);

            var effectiveRpm = realmConfig?.Scim?.RequestsPerMinute ?? scimOptions.RequestsPerMinute;
            if (effectiveRpm <= 0)
            {
                effectiveRpm = ScimOptions.DefaultRequestsPerMinute;
            }

            var tokensPerSecond = Math.Max(1, (int)Math.Ceiling(effectiveRpm / 60.0));
            var limitResult = await rateLimiter.CheckPostAuthSidAsync(
                $"scim-client:{realmId}",
                new PostAuthSidRateLimitOptions
                {
                    TokenBucketCapacity = effectiveRpm,
                    TokensPerSecond = tokensPerSecond
                },
                ct).ConfigureAwait(false);

            if (!limitResult.Allowed)
            {
                httpContext.Response.Headers.RetryAfter = Math.Max(1, limitResult.RetryAfterSeconds)
                    .ToString(CultureInfo.InvariantCulture);
                return ScimErrorResult.RateLimited();
            }
        }

        if (httpContext.User?.Identity?.IsAuthenticated == true
            && !string.IsNullOrWhiteSpace(realmId)
            && httpContext.RequestServices?.GetService<IScimSyncStateTracker>() is { } tracker)
        {
            await tracker.RecordAuthenticatedContactAsync(
                httpContext.User.GetTenantId(),
                realmId,
                isWrite: !HttpMethods.IsGet(httpContext.Request.Method),
                ct).ConfigureAwait(false);
        }

        return null;
    }

    private static bool IsWriteMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method);

    private static string? ResolveRealmId(HttpContext httpContext)
    {
        if (httpContext.Request.RouteValues.TryGetValue("realmId", out var raw)
            && raw is string realmId
            && !string.IsNullOrWhiteSpace(realmId))
        {
            return realmId;
        }

        return httpContext.User?.FindFirst("autheris:realm")?.Value;
    }
}
