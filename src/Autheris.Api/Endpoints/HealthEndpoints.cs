namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Api.Endpoints.Health;
using Autheris.Api.Extensions;
using Autheris.Api.Hosting.Warmup;
using Autheris.GraphQL.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

public static class HealthEndpoints
{
    /// <summary>
    /// Security mode reported in the Development health details: any DANGER entry -> "INSECURE_DEV_MODE",
    /// only WARN entries -> "STRICT_WITH_WARNINGS", none -> "STRICT_ZERO_TRUST".
    /// </summary>
    internal static string GetSecurityMode(GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(gatewayOptions);
        if (gatewayOptions.HasAnyDangerBypassActive)
        {
            return "INSECURE_DEV_MODE";
        }

        return gatewayOptions.HasAnyWarningActive ? "STRICT_WITH_WARNINGS" : "STRICT_ZERO_TRUST";
    }

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions, IHostEnvironment environment)
    {
        app.MapGet("/health/live", () =>
        {
            if (environment.IsDevelopment())
            {
                return Results.Ok(new
                {
                    status = "Live",
                    timestamp = DateTimeOffset.UtcNow,
                    securityMode = GetSecurityMode(gatewayOptions)
                });
            }

            return Results.Ok(new
            {
                status = "Live",
                timestamp = DateTimeOffset.UtcNow
            });
        }).AllowAnonymous().WithAuditExemption("Operational health probe");

        app.MapGet("/health/ready", (
            ITrafficDrainController controller,
            [FromServices] ReadinessProbe? readinessProbe,
            [FromServices] IGraphQlExecutorGate? executorGate,
            [FromServices] IStartupWarmupState? warmupState,
            CancellationToken ct) => HandleReadyAsync(controller, readinessProbe, executorGate, warmupState, gatewayOptions, environment, ct))
        .AllowAnonymous().WithAuditExemption("Operational health probe");

        // Issue #802: whether the served GraphQL schema is behind the catalog, for clients that query right after a catalog
        // write. Deliberately separate from /health/ready, which must not flap to 503 during every routine schema rebuild.
        app.MapGet("/health/graphql-schema", (
            [FromServices] ICatalogSchemaFreshness? freshness,
            HttpContext context,
            CancellationToken ct) => HandleGraphQlSchemaAsync(freshness, context, ct))
        .AllowAnonymous().WithAuditExemption("Operational health probe");

        return app;
    }

    /// <summary>
    /// Issue #802: 200 <c>{"status":"Current"}</c> or 503 <c>{"status":"Updating"}</c> with <c>Retry-After: 1</c>; 404 when
    /// the GraphQL catalog module is not registered. The body is the same in every environment and never carries catalog
    /// names, counts or versions (SEC-802-1).
    /// </summary>
    internal static async Task<IResult> HandleGraphQlSchemaAsync(ICatalogSchemaFreshness? freshness, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (freshness is null)
        {
            return Results.NotFound();
        }

        if (await freshness.IsSchemaCurrentAsync(ct).ConfigureAwait(false))
        {
            return Results.Ok(new { status = "Current" });
        }

        context.Response.Headers.RetryAfter = "1";
        return Results.Json(new { status = "Updating" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    internal static async Task<IResult> HandleReadyAsync(
        ITrafficDrainController controller,
        ReadinessProbe? readinessProbe,
        IGraphQlExecutorGate? executorGate,
        IStartupWarmupState? warmupState,
        GatewayOptions gatewayOptions,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        if (controller.IsDraining)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (readinessProbe != null)
        {
            var report = await readinessProbe.CheckHealthAsync(ct).ConfigureAwait(false);
            if (!report.IsHealthy)
            {
                if (environment.IsDevelopment())
                {
                    return Results.Json(new
                    {
                        status = "Unhealthy",
                        timestamp = DateTimeOffset.UtcNow,
                        securityMode = GetSecurityMode(gatewayOptions),
                        activeBypasses = gatewayOptions.GetAllActiveBypasses(),
                        activeWarnings = gatewayOptions.GetActiveWarnings(),
                        components = report.Components
                    }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return Results.Json(new
                {
                    status = "Unhealthy",
                    timestamp = DateTimeOffset.UtcNow
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        var gateState = executorGate?.State ?? ExecutorGateState.Pending;
        if (gateState != ExecutorGateState.Ready)
        {
            return NotReady(gateState == ExecutorGateState.Pending ? "Warming" : "Unhealthy", gateState, warmupState?.Phase);
        }

        var phase = warmupState?.Phase ?? WarmupPhase.Pending;
        if (phase != WarmupPhase.Ready)
        {
            return NotReady("Warming", gateState, phase);
        }

        if (environment.IsDevelopment())
        {
            return Results.Ok(new
            {
                status = "Ready",
                timestamp = DateTimeOffset.UtcNow,
                securityMode = GetSecurityMode(gatewayOptions),
                activeBypasses = gatewayOptions.GetAllActiveBypasses(),
                activeWarnings = gatewayOptions.GetActiveWarnings()
            });
        }

        return Results.Ok(new
        {
            status = "Ready",
            timestamp = DateTimeOffset.UtcNow
        });

        IResult NotReady(string status, ExecutorGateState gate, WarmupPhase? phase)
        {
            if (environment.IsDevelopment())
            {
                return Results.Json(new
                {
                    status,
                    timestamp = DateTimeOffset.UtcNow,
                    securityMode = GetSecurityMode(gatewayOptions),
                    executorGate = gate.ToString(),
                    warmupPhase = phase?.ToString() ?? "Unknown"
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Json(new { status, timestamp = DateTimeOffset.UtcNow }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
