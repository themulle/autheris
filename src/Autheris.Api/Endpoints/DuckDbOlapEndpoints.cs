using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
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
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await httpContext.Response.WriteAsJsonAsync(new
            {
                columns = result.Columns,
                rows = result.Rows,
                totalRows = result.TotalRowCount,
                executionDurationMs = result.ExecutionDuration.TotalMilliseconds
            }, ct);
        }
        else if (outcome.Details != null)
        {
            await httpContext.Response.WriteAsJsonAsync(new { error = outcome.Error, details = outcome.Details }, ct);
        }
        else
        {
            await httpContext.Response.WriteAsJsonAsync(new { error = outcome.Error }, ct);
        }
    }
}
