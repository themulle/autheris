namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Extensions;
using Autheris.Application.Data.Changes;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

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
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
        {
            return SafeErrorResults.BadRequest("INVALID_DATASET", "Domain, schema, and table must be specified.", httpContext);
        }

        var tableId = new TableIdentifier(domain, schema, table);
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var ifNoneMatch = httpContext.Request.Headers.IfNoneMatch.ToString();

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
            SafeErrorResults.LogFailure(httpContext, ex, ex.ErrorCode);
            return SafeErrorResults.Problem(StatusCodes.Status404NotFound, ex.ErrorCode, "Requested dataset was not found.", httpContext);
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "FORBIDDEN");
            return SafeErrorResults.Problem(StatusCodes.Status403Forbidden, "FORBIDDEN", "Access denied by data governance policy.", httpContext);
        }
        catch (GatewaySecurityException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, ex.ErrorCode);
            return SafeErrorResults.Problem(StatusCodes.Status403Forbidden, ex.ErrorCode, "Security policy rejected the change-token request.", httpContext);
        }
    }

    internal static async Task<IResult> HandlePostChangesSinceAsync(
        string domain,
        string schema,
        string table,
        [FromBody] ChangesSinceHttpRequest? body,
        HttpContext httpContext,
        IGovernedChangeDetectionService changeService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
        {
            return SafeErrorResults.BadRequest("INVALID_DATASET", "Domain, schema, and table must be specified.", httpContext);
        }

        var tableId = new TableIdentifier(domain, schema, table);
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();

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
            SafeErrorResults.LogFailure(httpContext, ex, ex.ErrorCode);
            return SafeErrorResults.Problem(StatusCodes.Status404NotFound, ex.ErrorCode, "Requested dataset was not found.", httpContext);
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "FORBIDDEN");
            return SafeErrorResults.Problem(StatusCodes.Status403Forbidden, "FORBIDDEN", "Access denied by data governance policy.", httpContext);
        }
        catch (GatewayInvalidQueryException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, ex.ErrorCode);
            return SafeErrorResults.BadRequest(ex.ErrorCode, "Invalid change-tracking query request.", httpContext);
        }
        catch (GatewaySecurityException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, ex.ErrorCode);
            int status = ex.ErrorCode is ChangeDetectionErrorCodes.CursorExpired or ChangeDetectionErrorCodes.CursorInvalid
                ? StatusCodes.Status410Gone
                : StatusCodes.Status403Forbidden;
            return SafeErrorResults.Problem(status, ex.ErrorCode, "Change cursor validation or governance check failed.", httpContext);
        }
    }
}
