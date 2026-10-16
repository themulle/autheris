using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Autheris.Api.Extensions;
using Autheris.Domain.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Endpoints;

public static class DuckDbOlapEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IEndpointRouteBuilder MapDuckDbOlapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/olap/query", HandleOlapQueryAsync)
           .WithName("ExecuteDuckDbOlapQueryV1")
           .WithTags("Analytics & OLAP")
           .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        return app;
    }

    internal static async Task HandleOlapQueryAsync(
        HttpContext httpContext,
        IOlapStagingService stagingService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.Olap");
        var ct = httpContext.RequestAborted;
        var env = httpContext.RequestServices?.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
        bool isDev = env?.IsDevelopment() ?? false;

        var options = gatewayOptions.Value.DuckDbOlap ?? new DuckDbOlapOptions();
        if (!options.Enabled)
        {
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await httpContext.Response.WriteAsJsonAsync(new { error = "In-memory DuckDB OLAP engine is currently disabled." }, ct);
            return;
        }

        var user = httpContext.User;
        if (user?.Identity?.IsAuthenticated != true &&
            !gatewayOptions.Value.IsQuickstartProfile)
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Authentication required for analytical OLAP execution." }, ct);
            return;
        }

        OlapQueryCommand? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<OlapQueryCommand>(httpContext.Request.Body, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid JSON payload in OLAP query request.");
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid JSON payload in request body." }, ct);
            return;
        }

        if (dto == null || string.IsNullOrWhiteSpace(dto.Sql))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Missing 'sql' query in request body." }, ct);
            return;
        }

        var secContext = EndpointSecurity.GetSecurityContext(httpContext);
        var outcome = await stagingService.ExecuteAsync(
            user ?? new ClaimsPrincipal(new ClaimsIdentity()), secContext, dto, isDev, httpContext.TraceIdentifier, ct).ConfigureAwait(false);

        httpContext.Response.StatusCode = outcome.StatusCode;
        if (outcome.Result is { } result)
        {
            try
            {
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    columns = result.Columns,
                    rows = result.Rows,
                    totalRows = result.TotalRowCount,
                    executionDurationMs = result.ExecutionDuration.TotalMilliseconds
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Request aborted by caller.
            }
            catch (Exception ex)
            {
                // Same contract as before the extraction: a failure while writing the result is a 400 with a generic error.
                // SEC-FED-DELTA-01: type and trace id only above Debug; the message can quote data values.
                logger.LogError("Error executing DuckDB OLAP query ({ExceptionType}). TraceId={TraceId}", ex.GetType().Name, httpContext.TraceIdentifier);
                logger.LogDebug(ex, "Error executing DuckDB OLAP query (full exception). TraceId={TraceId}", httpContext.TraceIdentifier);
                if (!httpContext.Response.HasStarted)
                {
                    httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                    if (isDev || gatewayOptions.Value.IsQuickstartProfile)
                    {
                        await httpContext.Response.WriteAsJsonAsync(new { error = "Analytical query execution failed.", details = ex.Message }, ct);
                    }
                    else
                    {
                        await httpContext.Response.WriteAsJsonAsync(new { error = "Analytical query execution failed." }, ct);
                    }
                }
            }
        }
        else if (outcome.Details != null)
        {
            await httpContext.Response.WriteAsJsonAsync(new { error = outcome.Error, code = outcome.ErrorCode, details = outcome.Details }, ct);
        }
        else if (outcome.ErrorCode != null)
        {
            await httpContext.Response.WriteAsJsonAsync(new { error = outcome.Error, code = outcome.ErrorCode }, ct);
        }
        else
        {
            await httpContext.Response.WriteAsJsonAsync(new { error = outcome.Error }, ct);
        }
    }
}
