namespace Autheris.Api.Security;

using System;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Source where the target object identifier is extracted from for ReBAC evaluation.
/// </summary>
public enum RebacParameterSource
{
    Route,
    Query,
    Header
}

/// <summary>
/// F-SEC-04: ASP.NET Core Endpoint Filter enforcing Google Zanzibar ReBAC authorization on HTTP / REST endpoints.
/// </summary>
public sealed class RebacEndpointFilter : IEndpointFilter
{
    private readonly string _relation;
    private readonly string _objectType;
    private readonly string _paramName;
    private readonly RebacParameterSource _source;

    public RebacEndpointFilter(
        string relation,
        string objectType,
        string paramName = "id",
        RebacParameterSource source = RebacParameterSource.Route)
    {
        _relation = relation ?? throw new ArgumentNullException(nameof(relation));
        _objectType = objectType ?? throw new ArgumentNullException(nameof(objectType));
        _paramName = paramName ?? throw new ArgumentNullException(nameof(paramName));
        _source = source;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var options = httpContext.RequestServices.GetService<IOptions<GatewayOptions>>()?.Value?.Rebac;
        if (options is not null && !options.Enabled)
        {
            return await next(context).ConfigureAwait(false);
        }

        // 1. Resolve Identity
        var caller = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(caller))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "Authentication required for ReBAC endpoint.");
        }

        // 2. Resolve Tenant
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);

        // 3. Resolve Target Object ID
        string? objectId = null;
        if (_source == RebacParameterSource.Route && httpContext.Request.RouteValues.TryGetValue(_paramName, out var routeVal))
        {
            objectId = routeVal?.ToString();
        }
        else if (_source == RebacParameterSource.Query && httpContext.Request.Query.TryGetValue(_paramName, out var queryVal))
        {
            objectId = queryVal.ToString();
        }
        else if (_source == RebacParameterSource.Header && httpContext.Request.Headers.TryGetValue(_paramName, out var headerVal))
        {
            objectId = headerVal.ToString();
        }

        if (string.IsNullOrWhiteSpace(objectId))
        {
            // Fail-closed default
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Target object identifier missing for ReBAC check.");
        }

        if (string.Equals(_objectType, "table", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var norm = TableIdentifierNormalizer.Normalize(objectId);
                objectId = norm.ToQualifiedName();
            }
            catch
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Bad Request",
                    detail: $"Invalid table identifier '{objectId}'.");
            }
        }

        var targetObject = $"{_objectType}:{objectId}";

        // 4. Batch DataLoader / Evaluator Check
        var loader = httpContext.RequestServices.GetRequiredService<IRebacBatchDataLoader>();
        var isAllowed = await loader.CheckAsync(tenant.Value, caller, _relation, targetObject, httpContext.RequestAborted).ConfigureAwait(false);

        if (!isAllowed)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Access denied by ReBAC policy.");
        }

        return await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Fluent extensions for configuring ReBAC authorization on ASP.NET Core endpoints.
/// </summary>
public static class RebacEndpointExtensions
{
    public static RouteHandlerBuilder RequireRebac(
        this RouteHandlerBuilder builder,
        string relation,
        string objectType,
        string paramName = "id",
        RebacParameterSource source = RebacParameterSource.Route)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(new RebacEndpointFilter(relation, objectType, paramName, source));
    }
}
