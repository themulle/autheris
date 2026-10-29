using System.Diagnostics;
using System.Globalization;
using Autheris.Api.Errors;
using Autheris.Application.Common;
using Autheris.Application.Sql.Errors;
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

        var traceId = Activity.Current?.TraceId.ToHexString() ?? httpContext.TraceIdentifier;

        if (Activity.Current != null && Activity.Current.IsAllDataRequested)
        {
            var errType = Autheris.Domain.Diagnostics.TelemetryExceptionPolicy.ErrorType(exception);
            Activity.Current.SetStatus(ActivityStatusCode.Error, errType);
            Activity.Current.SetTag(Autheris.Domain.Diagnostics.TelemetryAttributes.ErrorType, errType);
        }

        if (httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request {Path} cancelled by the client ({ExceptionType}). TraceId={TraceId}", httpContext.Request.Path, exception.GetType().Name, traceId);
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        // WP-X0a: compiler, policy and DML errors carry a stable `code` and the fixed message of that code.
        string? code = null;
        string? limit = null;
        (int status, string title, int? retryAfter) = (0, string.Empty, null);
        if (SqlCompilerErrorMapper.TryMap(exception, out var mapped))
        {
            SqlCompilerErrorMapper.RecordOutcome(mapped);
            (status, title, retryAfter) = (mapped.Status, mapped.Title, mapped.Status == StatusCodes.Status503ServiceUnavailable ? UnavailableRetryAfterSeconds : null);
            code = mapped.Code;
            limit = mapped.Limit;
        }
        else if (exception is GatewayThrottledException throttled)
        {
            status = StatusCodes.Status429TooManyRequests;
            title = "Too many concurrent requests. Retry later.";
            retryAfter = throttled.RetryAfterSeconds;
            code = "TOO_MANY_REQUESTS";
        }
        else
        {
            (status, title, retryAfter) = Map(exception);
            code = (exception as GatewaySecurityException) is { } denied
                ? GatewayDenialStatusMapper.SpecificCode(denied) ?? CodeForStatus(status)
                : CodeForStatus(status);

            // DEF-02: a data-integrity refusal of a consent is a deliberate deny (403, fixed title); the governed code tells operators which one.
            if (exception is ConsentTenantIntegrityException)
            {
                code = ApiErrorCodes.ConsentTenantInvalid;
            }
        }

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
        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        if (retryAfter.HasValue)
        {
            problem.Extensions["retryAfterSeconds"] = retryAfter.Value;
        }

        if (limit is not null)
        {
            problem.Extensions["limit"] = limit;
        }

        problem.Extensions["traceId"] = traceId;

        await httpContext.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static (int Status, string Title, int? RetryAfter) Denial(GatewaySecurityException denial)
    {
        var (status, title) = GatewayDenialStatusMapper.Map(denial);
        return (status, title, null);
    }

    /// <summary>SEC-PG-14: every problem carries a stable code derived from its status only, never from exception data.</summary>
    private static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ApiErrorCodes.BadRequest,
        StatusCodes.Status401Unauthorized => ApiErrorCodes.Unauthorized,
        StatusCodes.Status403Forbidden => ApiErrorCodes.AccessDenied,
        StatusCodes.Status408RequestTimeout => ApiErrorCodes.RequestTimeout,
        StatusCodes.Status413PayloadTooLarge => ApiErrorCodes.PayloadTooLarge,
        StatusCodes.Status414UriTooLong => ApiErrorCodes.UriTooLong,
        StatusCodes.Status431RequestHeaderFieldsTooLarge => ApiErrorCodes.RequestHeaderFieldsTooLarge,
        StatusCodes.Status429TooManyRequests => ApiErrorCodes.TooManyRequests,
        StatusCodes.Status501NotImplemented => ApiErrorCodes.NotImplemented,
        StatusCodes.Status503ServiceUnavailable => ApiErrorCodes.ServiceUnavailable,
        StatusCodes.Status504GatewayTimeout => ApiErrorCodes.GatewayTimeout,
        _ => ApiErrorCodes.InternalError
    };

    private static (int Status, string Title, int? RetryAfter) Map(Exception exception) => exception switch
    {
        // SEC-PG-14: the framework message names the binding parameter; only the status survives, with a fixed text.
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => (StatusCodes.Status413PayloadTooLarge, "The request is too large.", null),
        BadHttpRequestException { StatusCode: StatusCodes.Status408RequestTimeout } => (StatusCodes.Status408RequestTimeout, "The request timed out.", null),
        BadHttpRequestException { StatusCode: StatusCodes.Status414UriTooLong } => (StatusCodes.Status414UriTooLong, "The request URI is too long.", null),
        BadHttpRequestException { StatusCode: StatusCodes.Status431RequestHeaderFieldsTooLarge } => (StatusCodes.Status431RequestHeaderFieldsTooLarge, "The request headers are too large.", null),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, "The request is malformed.", null),
        GatewayInvalidQueryException e => (StatusCodes.Status400BadRequest, e.Message, null),
        GatewayThrottledException e => (StatusCodes.Status429TooManyRequests, "Too many concurrent requests. Retry later.", e.RetryAfterSeconds),
        Autheris.Application.Audit.AuditUnavailableException => (StatusCodes.Status503ServiceUnavailable, "The audit store is temporarily unavailable. Retry later.", UnavailableRetryAfterSeconds),
        // API-06: migrations still pending: fail closed with a retryable 503 instead of a 500 plus error log.
        Autheris.Infrastructure.Persistence.Migrations.GovernanceStoreNotReadyException => (StatusCodes.Status503ServiceUnavailable, "The governance store is not ready yet. Retry later.", UnavailableRetryAfterSeconds),
        GatewayNotImplementedException => (StatusCodes.Status501NotImplemented, "The requested feature or data source capability is not implemented.", null),
        GatewayUnsupportedColumnTypeException => (StatusCodes.Status501NotImplemented, "The requested column type is not supported.", null),
        NotSupportedException => (StatusCodes.Status501NotImplemented, "The requested operation is not supported.", null),
        Antlr4.Runtime.Misc.ParseCanceledException => (StatusCodes.Status400BadRequest, "Invalid SQL syntax.", null),
        Antlr4.Runtime.RecognitionException => (StatusCodes.Status400BadRequest, "Invalid SQL syntax.", null),
        Exception e when e.InnerException is Antlr4.Runtime.Misc.ParseCanceledException or Antlr4.Runtime.RecognitionException =>
            (StatusCodes.Status400BadRequest, "Invalid SQL syntax.", null),
        GatewaySecurityException denial => Denial(denial),
        _ => DataAccessErrorClassifier.Classify(exception) switch
        {
            DataAccessErrorKind.Timeout => (StatusCodes.Status504GatewayTimeout, "The request exceeded the execution time limit.", null),
            DataAccessErrorKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "The data source is temporarily unavailable. Retry later.", UnavailableRetryAfterSeconds),
            _ => (StatusCodes.Status500InternalServerError, "The request could not be processed.", (int?)null)
        }
    };
}
