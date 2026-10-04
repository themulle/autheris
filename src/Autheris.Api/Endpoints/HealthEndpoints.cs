namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
        }).AllowAnonymous();

        app.MapGet("/health/ready", async (
            ITrafficDrainController controller,
            IGatewayHealthCheckService? healthCheckService,
            CancellationToken ct) =>
        {
            if (controller.IsDraining)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (healthCheckService != null)
            {
                var report = await healthCheckService.CheckHealthAsync(ct).ConfigureAwait(false);
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
        }).AllowAnonymous();

        return app;
    }
}
