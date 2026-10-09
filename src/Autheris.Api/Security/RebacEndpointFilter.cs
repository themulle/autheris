namespace Autheris.Api.Security;

using System;
using System.Text.Json;
using System.Threading;
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
    Header,

    /// <summary>
    /// The query string first, otherwise a top-level string property of the JSON request body (WebSQL findings 4.3).
    /// The body is buffered and rewound, so the handler can read it again.
    /// </summary>
    QueryOrJsonBody
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
        var gatewayOptions = httpContext.RequestServices.GetService<IOptions<GatewayOptions>>()?.Value;
        var options = gatewayOptions?.Rebac;
        if ((options is not null && !options.Enabled) || gatewayOptions?.IsRebacBypassed == true)
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
        else if (_source == RebacParameterSource.QueryOrJsonBody)
        {
            if (httpContext.Request.Query.TryGetValue(_paramName, out var queryOrBodyVal) && !string.IsNullOrWhiteSpace(queryOrBodyVal))
            {
                objectId = queryOrBodyVal.ToString();
            }
            else
            {
                var bodyResult = await ReadJsonBodyPropertyAsync(httpContext.Request, _paramName, httpContext.RequestAborted).ConfigureAwait(false);
                if (bodyResult.HasDuplicateProperties)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Bad Request",
                        detail: "Duplicate properties in JSON request body are not permitted.");
                }

                objectId = bodyResult.Value;
            }
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

        httpContext.Items[$"RebacValidated:{_paramName}"] = objectId;

        return await next(context).ConfigureAwait(false);
    }

    private readonly record struct JsonBodyReadResult(string? Value, bool HasDuplicateProperties);

    /// <summary>
    /// Top-level string property <paramref name="name"/> (case-insensitive) of a JSON body; returns result indicating
    /// matched value and whether duplicate properties were detected. The body is rewound for the handler.
    /// </summary>
    private static async Task<JsonBodyReadResult> ReadJsonBodyPropertyAsync(HttpRequest request, string name, CancellationToken ct)
    {
        if (request.ContentType == null || !request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            string? matchedValue = null;
            var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seenProperties.Add(property.Name))
                {
                    return new JsonBodyReadResult(null, HasDuplicateProperties: true);
                }

                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    matchedValue = property.Value.GetString();
                }
            }

            return new JsonBodyReadResult(matchedValue, HasDuplicateProperties: false);
        }
        catch (JsonException)
        {
            return default;
        }
        finally
        {
            request.Body.Position = 0;
        }
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
