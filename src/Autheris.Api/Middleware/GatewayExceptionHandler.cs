using System.Diagnostics;
using System.Globalization;
using Autheris.Application.Common;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Autheris.Api.Middleware;

/// <summary>
/// O9 (docs/plans/rls-subquery-in-strategy.md, OData-Härtung): last line of defence for exceptions no endpoint handled.
/// Registered with <c>UseExceptionHandler()</c> inside the pipeline, so the developer exception page that ASP.NET Core
/// adds in Development never sees an exception. Answers with application/problem+json: status, generic title and a
/// trace id; never exception text, stack trace, source paths or database error messages.
/// </summary>
public sealed class GatewayExceptionHandler(ILogger<GatewayExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Non-standard status used by nginx and others for "client closed request".</summary>
    internal const int ClientClosedRequest = 499;

    private const int UnavailableRetryAfterSeconds = 5;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (httpContext.Response.HasStarted)
        {
            logger.LogError(exception, "Unhandled exception after the response started for {Path}.", httpContext.Request.Path);
            return false;
        }

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request {Path} cancelled by the client ({ExceptionType}). TraceId={TraceId}", httpContext.Request.Path, exception.GetType().Name, traceId);
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        var (status, title, retryAfter) = Map(exception);
        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception for {Path} mapped to {Status}. TraceId={TraceId}", httpContext.Request.Path, status, traceId);
        }
        else
        {
            logger.LogWarning("Request {Path} rejected with {Status} ({ExceptionType}). TraceId={TraceId}", httpContext.Request.Path, status, exception.GetType().Name, traceId);
        }

        httpContext.Response.Clear();
        httpContext.Response.StatusCode = status;
        if (retryAfter.HasValue)
        {
            httpContext.Response.Headers.RetryAfter = retryAfter.Value.ToString(CultureInfo.InvariantCulture);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://httpwg.org/specs/rfc9110.html#status.{status}"
        };
        problem.Extensions["traceId"] = traceId;

        await httpContext.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static (int Status, string Title, int? RetryAfter) Map(Exception exception) => exception switch
    {
        GatewayInvalidQueryException e => (StatusCodes.Status400BadRequest, e.Message, null),
        GatewayThrottledException e => (StatusCodes.Status429TooManyRequests, "Too many concurrent requests. Retry later.", e.RetryAfterSeconds),
        GatewayUnsupportedColumnTypeException e => (StatusCodes.Status501NotImplemented, e.Message, null),
        GatewayUnauthorizedException => (StatusCodes.Status401Unauthorized, "Authentication required.", null),
        GatewaySecurityException { ErrorCode: "RESPONSE_TOO_LARGE" } => (StatusCodes.Status400BadRequest, "The response exceeds the size limit.", null),
        GatewaySecurityException => (StatusCodes.Status403Forbidden, "Access denied.", null),
        _ => DataAccessErrorClassifier.Classify(exception) switch
        {
            DataAccessErrorKind.Timeout => (StatusCodes.Status504GatewayTimeout, "The request exceeded the execution time limit.", null),
            DataAccessErrorKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "The data source is temporarily unavailable. Retry later.", UnavailableRetryAfterSeconds),
            _ => (StatusCodes.Status500InternalServerError, "The request could not be processed.", (int?)null)
        }
    };
}
