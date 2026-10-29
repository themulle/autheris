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

    public static IResult TooManyRequests(string code, string detail, HttpContext? httpContext = null, int? retryAfterSeconds = null, IDictionary<string, object?>? extraExtensions = null)
    {
        if (httpContext != null && retryAfterSeconds is > 0)
        {
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Problem(StatusCodes.Status429TooManyRequests, code, detail, httpContext, title: "Too Many Requests", extraExtensions: extraExtensions);
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
        ArgumentNullException.ThrowIfNull(detail);
        code = ApiErrorCodes.Resolve(statusCode, code);

        var traceId = ResolveTraceId(httpContext);
        var correlationId = ResolveCorrelationId(httpContext, traceId);

        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["correlationId"] = correlationId,
            ["traceId"] = traceId
        };

        AddAllowedExtensions(extensions, extraExtensions);

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
        string? title = null,
        IReadOnlyDictionary<string, object?>? ext = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        code = ApiErrorCodes.Resolve(statusCode, code);
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
        AddAllowedExtensions(problem.Extensions, ext);
        return problem;
    }

    /// <summary>Upper bound of operator-declared text that may reach <c>detail</c> (NL-SC-30).</summary>
    public const int MaxOperatorTextLength = 256;

    /// <summary>Strips control characters, trims and caps operator-declared text before it reaches a body (NL-SC-30).</summary>
    public static string SanitizeOperatorText(string? text, string fallback) =>
        Autheris.Application.Common.ErrorText.Sanitize(text, fallback, MaxOperatorTextLength);

    /// <summary>
    /// The only raw-stream writer of a governed problem body (NL-SC-27): the code is checked against the governed set (status default
    /// otherwise) and extension members are limited to the allowlist.
    /// </summary>
    public static Task WriteAsync(
        HttpContext httpContext,
        int status,
        string code,
        string detail,
        CancellationToken ct,
        IReadOnlyDictionary<string, object?>? ext = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = status;
        return httpContext.Response.WriteAsJsonAsync(
            CreateProblemDetails(status, code, detail, httpContext, ext: ext),
            (System.Text.Json.JsonSerializerOptions?)null,
            "application/problem+json",
            ct);
    }

    /// <summary>Raw-stream 429: sets <c>Retry-After</c> and the <c>retryAfterSeconds</c> member consistently, then writes the governed body.</summary>
    public static Task WriteTooManyRequests(HttpContext httpContext, string code, string detail, int retryAfterSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return WriteAsync(httpContext, StatusCodes.Status429TooManyRequests, code, detail, ct,
            new Dictionary<string, object?> { ["retryAfterSeconds"] = retryAfterSeconds });
    }

    /// <summary>
    /// A problem body for an anti-enumeration pair (NL-SC-28): the bytes are fixed per (status, code, detail), carry no traceId,
    /// instance or correlationId, and no header is added, so members of the pair cannot be told apart.
    /// </summary>
    public static IResult Indistinguishable(int status, string code, string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var resolved = ApiErrorCodes.Resolve(status, code);
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["type"] = $"urn:autheris:error:{resolved}",
            ["title"] = ResolveDefaultTitle(status),
            ["status"] = status,
            ["detail"] = detail,
            ["code"] = resolved
        });
        return new FixedProblemResult(status, body);
    }

    private sealed class FixedProblemResult(int status, byte[] body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = "application/problem+json";
            httpContext.Response.ContentLength = body.Length;
            return httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted).AsTask();
        }
    }

    // NL-SC-27: the only extension members a problem body may carry, scalar values only.
    private static readonly HashSet<string> AllowedExtensionKeys = new(StringComparer.Ordinal)
    {
        "retryAfterSeconds", "maxValidTo", "phase", "sunsetDate", "replacement", "limit", "discardedEvents",
        // NL-SC-32: privacy-budget 429 figures (typed values of the budget decision, caller's own client id only).
        "clientId", "consumed", "totalBudget"
    };

    private static void AddAllowedExtensions(IDictionary<string, object?> target, IEnumerable<KeyValuePair<string, object?>>? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            if (AllowedExtensionKeys.Contains(key) && IsScalar(value))
            {
                target[key] = value;
            }
        }
    }

    private static bool IsScalar(object? value) => value is null or string or bool or sbyte or byte or short or ushort or int or uint
        or long or ulong or float or double or decimal or DateTime or DateTimeOffset or DateOnly or TimeSpan;

    /// <summary>Logs the original exception server-side (never returned to the client).</summary>
    public static void LogFailure(HttpContext? context, Exception exception, string code)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var logger = context?.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);
        var traceId = ResolveTraceId(context);
        logger?.LogWarning(exception, "Request failed with code {ErrorCode} (correlation {CorrelationId}, trace {TraceId}).", code, ResolveCorrelationId(context, traceId), traceId);
    }

    /// <summary>
    /// Logs a refusal with its code and exception type name only (no exception object, message or inner exception), for refusals whose
    /// internals must stay out of every channel (#389 I389-SC-02).
    /// </summary>
    public static void LogRefusal(HttpContext? context, Type exceptionType, string code)
    {
        ArgumentNullException.ThrowIfNull(exceptionType);
        var logger = context?.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);
        var traceId = ResolveTraceId(context);
        logger?.LogWarning("Request refused with code {ErrorCode} ({ExceptionType}) (correlation {CorrelationId}, trace {TraceId}).", code, exceptionType.Name, ResolveCorrelationId(context, traceId), traceId);
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
