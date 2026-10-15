namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Data.Changes;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// HTTP payload for <c>POST /api/v1/data/{domain}/{schema}/{table}/changes</c> (#77).
/// </summary>
public sealed record ChangesSinceHttpRequest(
    string? Cursor = null,
    IReadOnlyList<Dictionary<string, object?>>? KnownKeys = null,
    IReadOnlyList<string>? SelectColumns = null);

/// <summary>
/// Issue #77: Governed change detection (<c>ETag</c> / <c>If-None-Match</c>) and pull-based <c>changesSince</c> REST endpoints.
/// </summary>
public static class ChangeDetectionEndpoints
{
    public static IEndpointRouteBuilder MapChangeDetectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/data/{domain}/{schema}/{table}/change-token", HandleGetChangeTokenAsync)
            .WithName("GetDatasetChangeToken")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        app.MapPost("/api/v1/data/{domain}/{schema}/{table}/changes", HandlePostChangesSinceAsync)
            .WithName("QueryDatasetChangesSince")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        return app;
    }

    internal static async Task<IResult> HandleGetChangeTokenAsync(
        string domain,
        string schema,
        string table,
        HttpContext httpContext,
        IGovernedChangeDetectionService changeService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
        {
            return Results.BadRequest(new { error = "Domain, schema, and table must be specified." });
        }

        var tableId = new TableIdentifier(domain, schema, table);
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var ifNoneMatch = httpContext.Request.Headers.IfNoneMatch.ToString();
        var logger = loggerFactory.CreateLogger(typeof(ChangeDetectionEndpoints));

        try
        {
            var result = await changeService.CheckTableModifiedAsync(
                tableId,
                httpContext.User,
                tenant,
                string.IsNullOrWhiteSpace(ifNoneMatch) ? null : ifNoneMatch,
                clientIp,
                ct).ConfigureAwait(false);

            if (result.ChangeToken != null)
            {
                httpContext.Response.Headers.ETag = result.ChangeToken;
            }

            if (result.NotModified)
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Ok(new
            {
                dataset = tableId.ToString(),
                supportsChangeTracking = result.SupportsChangeTracking,
                changeToken = result.ChangeToken,
                currentVersion = result.CurrentVersion,
                queryCost = result.QueryCost
            });
        }
        catch (TableNotFoundException ex)
        {
            logger.LogWarning("Dataset not found checking change token for {Table}: {Message}", tableId, ex.Message);
            return Results.NotFound(new { error = "Requested dataset was not found.", code = ex.ErrorCode });
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Access forbidden checking change token for {Table}: {Message}", tableId, ex.Message);
            return Results.Json(new { error = "Access denied by data governance policy.", code = "FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (GatewaySecurityException ex)
        {
            logger.LogWarning("Security exception checking change token for {Table}: {Code} - {Message}", tableId, ex.ErrorCode, ex.Message);
            return Results.Json(new { error = "Security policy rejected the change-token request.", code = ex.ErrorCode }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    internal static async Task<IResult> HandlePostChangesSinceAsync(
        string domain,
        string schema,
        string table,
        [FromBody] ChangesSinceHttpRequest? body,
        HttpContext httpContext,
        IGovernedChangeDetectionService changeService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
        {
            return Results.BadRequest(new { error = "Domain, schema, and table must be specified." });
        }

        var tableId = new TableIdentifier(domain, schema, table);
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var logger = loggerFactory.CreateLogger(typeof(ChangeDetectionEndpoints));

        IReadOnlyList<IReadOnlyDictionary<string, object?>>? knownKeys = null;
        if (body?.KnownKeys is { Count: > 0 })
        {
            var list = new List<IReadOnlyDictionary<string, object?>>(body.KnownKeys.Count);
            foreach (var item in body.KnownKeys)
            {
                list.Add(item);
            }

            knownKeys = list;
        }

        var request = new ChangesSinceRequest(
            Table: tableId,
            Cursor: body?.Cursor,
            User: httpContext.User,
            TenantId: tenant,
            KnownKeys: knownKeys,
            SelectColumns: body?.SelectColumns,
            ClientIp: clientIp);

        try
        {
            var response = await changeService.GetChangesSinceAsync(request, ct).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (TableNotFoundException ex)
        {
            logger.LogWarning("Dataset not found querying changesSince for {Table}: {Message}", tableId, ex.Message);
            return Results.NotFound(new { error = "Requested dataset was not found.", code = ex.ErrorCode });
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Access forbidden querying changesSince for {Table}: {Message}", tableId, ex.Message);
            return Results.Json(new { error = "Access denied by data governance policy.", code = "FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (GatewayInvalidQueryException ex)
        {
            logger.LogWarning("Invalid query in changesSince for {Table}: {Message}", tableId, ex.Message);
            return Results.BadRequest(new { error = "Invalid change-tracking query request.", code = ex.ErrorCode });
        }
        catch (GatewaySecurityException ex)
        {
            logger.LogWarning("Security exception in changesSince for {Table}: {Code} - {Message}", tableId, ex.ErrorCode, ex.Message);
            int status = ex.ErrorCode is ChangeDetectionErrorCodes.CursorExpired or ChangeDetectionErrorCodes.CursorInvalid
                ? StatusCodes.Status410Gone
                : StatusCodes.Status403Forbidden;
            return Results.Json(new { error = "Change cursor validation or governance check failed.", code = ex.ErrorCode }, statusCode: status);
        }
    }
}
