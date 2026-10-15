namespace Autheris.Api.Errors;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autheris.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Central RFC 7807 <c>application/problem+json</c> error factory (WP-ARC-C-05).
/// Produces standardized <see cref="ProblemDetails"/> responses with <c>urn:autheris:error:{code}</c> type URIs,
/// correlation/trace identifiers, and the Decision G-9 compatibility <c>error</c> extension property.
/// </summary>
public static class ApiProblems
{
    private const string LogCategory = "Autheris.Api.Errors.ApiProblems";

    public static IResult BadRequest(string code, string detail, HttpContext? httpContext = null, IDictionary<string, object?>? extraExtensions = null) =>
        Problem(StatusCodes.Status400BadRequest, code, detail, httpContext, title: "Bad Request", extraExtensions: extraExtensions);

    public static IResult Unauthorized(string code = ApiErrorCodes.Unauthorized, string detail = "Authentication is required to access this resource.", HttpContext? httpContext = null) =>
        Problem(StatusCodes.Status401Unauthorized, code, detail, httpContext, title: "Unauthorized");

    public static IResult Forbidden(string code = ApiErrorCodes.AccessDenied, string detail = "Access to this resource is denied by gateway security policy.", HttpContext? httpContext = null) =>
        Problem(StatusCodes.Status403Forbidden, code, detail, httpContext, title: "Forbidden");

    public static IResult NotFound(string code = ApiErrorCodes.ResourceNotFound, string detail = "The requested resource was not found.", HttpContext? httpContext = null) =>
        Problem(StatusCodes.Status404NotFound, code, detail, httpContext, title: "Not Found");

    public static IResult Conflict(string code, string detail, HttpContext? httpContext = null) =>
        Problem(StatusCodes.Status409Conflict, code, detail, httpContext, title: "Conflict");

    public static IResult PayloadTooLarge(string detail, HttpContext? httpContext = null, string code = ApiErrorCodes.PayloadTooLarge) =>
        Problem(StatusCodes.Status413PayloadTooLarge, code, detail, httpContext, title: "Payload Too Large");

    public static IResult UnsupportedMediaType(string detail, HttpContext? httpContext = null, string code = ApiErrorCodes.UnsupportedMediaType) =>
        Problem(StatusCodes.Status415UnsupportedMediaType, code, detail, httpContext, title: "Unsupported Media Type");

    public static IResult Unprocessable(string code, string detail, HttpContext? httpContext = null) =>
        Problem(StatusCodes.Status422UnprocessableEntity, code, detail, httpContext, title: "Unprocessable Entity");

    public static IResult TooManyRequests(string code, string detail, HttpContext? httpContext = null, int? retryAfterSeconds = null)
    {
        if (httpContext != null && retryAfterSeconds is > 0)
        {
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Problem(StatusCodes.Status429TooManyRequests, code, detail, httpContext, title: "Too Many Requests");
    }

    public static IResult ServiceUnavailable(string code = ApiErrorCodes.ServiceUnavailable, string detail = "The service is temporarily unavailable.", HttpContext? httpContext = null) =>
        Problem(StatusCodes.Status503ServiceUnavailable, code, detail, httpContext, title: "Service Unavailable");

    public static IResult Problem(
        int statusCode,
        string code,
        string detail,
        HttpContext? httpContext = null,
        string? title = null,
        IDictionary<string, object?>? extraExtensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(detail);

        var traceId = ResolveTraceId(httpContext);
        var correlationId = ResolveCorrelationId(httpContext, traceId);

        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["correlationId"] = correlationId,
            ["traceId"] = traceId,
            // Decision G-9: retain top-level "error" property in serialized ProblemDetails JSON for backward compatibility.
            ["error"] = detail
        };

        if (extraExtensions != null)
        {
            foreach (var (k, v) in extraExtensions)
            {
                extensions[k] = v;
            }
        }

        return Results.Problem(
            detail: detail,
            instance: httpContext?.Request.Path.Value,
            statusCode: statusCode,
            title: title ?? ResolveDefaultTitle(statusCode),
            type: $"urn:autheris:error:{code}",
            extensions: extensions);
    }

    public static ProblemDetails CreateProblemDetails(
        int statusCode,
        string code,
        string detail,
        HttpContext? httpContext = null,
        string? title = null)
    {
        var traceId = ResolveTraceId(httpContext);
        var correlationId = ResolveCorrelationId(httpContext, traceId);

        var problem = new ProblemDetails
        {
            Type = $"urn:autheris:error:{code}",
            Title = title ?? ResolveDefaultTitle(statusCode),
            Status = statusCode,
            Detail = detail,
            Instance = httpContext?.Request.Path.Value
        };

        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["traceId"] = traceId;
        problem.Extensions["error"] = detail;
        return problem;
    }

    /// <summary>Logs the original exception server-side (never returned to the client).</summary>
    public static void LogFailure(HttpContext? context, Exception exception, string code)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var logger = context?.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);
        logger?.LogWarning(exception, "Request failed with code {ErrorCode} (trace {TraceId}).", code, ResolveTraceId(context));
    }

    public static string ResolveTraceId(HttpContext? context) =>
        Activity.Current?.TraceId.ToString() ?? context?.TraceIdentifier ?? string.Empty;

    public static string ResolveCorrelationId(HttpContext? context, string? fallbackTraceId = null)
    {
        if (context != null)
        {
            if (context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var item) &&
                item is string cid &&
                !string.IsNullOrWhiteSpace(cid))
            {
                return cid;
            }

            if (context.Request.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName, out var headerVal) &&
                !string.IsNullOrWhiteSpace(headerVal))
            {
                return headerVal.ToString().Trim();
            }
        }

        return fallbackTraceId ?? ResolveTraceId(context);
    }

    private static string ResolveDefaultTitle(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status406NotAcceptable => "Not Acceptable",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status410Gone => "Gone",
        StatusCodes.Status413PayloadTooLarge => "Payload Too Large",
        StatusCodes.Status415UnsupportedMediaType => "Unsupported Media Type",
        StatusCodes.Status422UnprocessableEntity => "Unprocessable Entity",
        StatusCodes.Status429TooManyRequests => "Too Many Requests",
        StatusCodes.Status501NotImplemented => "Not Implemented",
        StatusCodes.Status502BadGateway => "Bad Gateway",
        StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
        StatusCodes.Status504GatewayTimeout => "Gateway Timeout",
        _ => "An error occurred"
    };
}
