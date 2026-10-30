using Autheris.Api.Errors;
using Autheris.Application.Common;
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
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.ServiceUnavailable, "In-memory DuckDB OLAP engine is currently disabled.", ct);
            return;
        }

        var user = httpContext.User;
        if (user?.Identity?.IsAuthenticated != true &&
            !gatewayOptions.Value.IsQuickstartProfile)
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "Authentication required for analytical OLAP execution.", ct);
            return;
        }

        OlapQueryCommand? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<OlapQueryCommand>(httpContext.Request.Body, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            logger.Warning(ex, "Invalid JSON payload in OLAP query request.");
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidJson, "Invalid JSON payload in request body.", ct);
            return;
        }

        if (dto == null || string.IsNullOrWhiteSpace(dto.Sql))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "Missing 'sql' query in request body.", ct);
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
                // A failure while writing the result is a 400 with a fixed problem body in every environment (ARCH-MNT-08).
                // SEC-FED-DELTA-01: type and trace id only above Debug; the message can quote data values.
                logger.Error(ex, "Error executing DuckDB OLAP query. TraceId={TraceId}", httpContext.TraceIdentifier);
                if (!httpContext.Response.HasStarted)
                {
                    httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                    var problem = ApiProblems.CreateProblemDetails(
                        StatusCodes.Status400BadRequest, ApiErrorCodes.OlapQueryFailed, "Analytical query execution failed.", httpContext);
                    httpContext.Response.ContentType = "application/problem+json";
                    await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", ct);
                }
                else
                {
                    // A partial stream is already on the wire: never append an error string, abort the connection instead.
                    httpContext.Abort();
                }
            }
        }
        else
        {
            // The outcome text can quote table names, counts or exception text: only a fixed detail per status reaches the body (NL-SC-21).
            await ApiProblems.WriteAsync(httpContext, outcome.StatusCode, outcome.ErrorCode ?? string.Empty, OutcomeDetail(outcome.StatusCode), ct);
        }
    }

    private static string OutcomeDetail(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "The analytical request could not be processed.",
        StatusCodes.Status401Unauthorized => "Authentication required for analytical OLAP execution.",
        StatusCodes.Status403Forbidden => "Access denied.",
        StatusCodes.Status404NotFound => "The requested resource was not found.",
        StatusCodes.Status429TooManyRequests => "OLAP capacity limit reached. Please retry later.",
        StatusCodes.Status502BadGateway => "The analytical request could not be completed.",
        StatusCodes.Status504GatewayTimeout => "The analytical request timed out.",
        _ => "The analytical request failed."
    };
}
