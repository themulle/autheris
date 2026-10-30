using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Autheris.Api.Middleware;

/// <summary>
/// The one HTTP mapping of a <see cref="GatewaySecurityException"/>, used by the global <see cref="GatewayExceptionHandler"/>
/// and by the WebSQL endpoint so that both channels answer the same status for the same denial (CR-02). The text is
/// generic; the stable error code only goes to the audit record, never to the response or to a log entry.
/// </summary>
internal static class GatewayDenialStatusMapper
{
    /// <summary>Status and generic title of the denial.</summary>
    internal static (int Status, string Title) Map(GatewaySecurityException exception) => exception switch
    {
        GatewayUnauthorizedException => (StatusCodes.Status401Unauthorized, "Authentication required."),
        { ErrorCode: "RESPONSE_TOO_LARGE" } => (StatusCodes.Status400BadRequest, "The response exceeds the size limit."),
        // Issue #107 (SEC-RF107-08): a failed key-set load is an availability problem, not a policy denial; fixed message, no table or rule.
        { ErrorCode: Autheris.Application.Sql.Policy.RowFilterKeySets.RowFilterKeySetErrorCodes.KeySetUnavailable } =>
            (StatusCodes.Status503ServiceUnavailable, Autheris.Application.Sql.Policy.RowFilterKeySets.RowFilterKeySetErrorCodes.UnavailableMessage),
        _ => (StatusCodes.Status403Forbidden, "Access denied.")
    };

    /// <summary>
    /// The WebSQL response of a denial: the status and fixed title of <see cref="Map"/> in the same problem body the global handler writes
    /// (<see cref="GatewayProblemWriter"/>, NL-T-30). Only the code and the exception type are logged, never the exception object: the
    /// inner exception of a key source failure may quote data values (C-8, SEC-11).
    /// </summary>
    internal static Task WriteWebSqlResponseAsync(
        HttpContext httpContext, GatewaySecurityException denial, ILogger logger, CancellationToken ct)
    {
        logger.LogWarning(
            "WebSQL denied by the gateway ({ErrorCode}, {ExceptionType}). TraceId={TraceId}",
            denial.ErrorCode, denial.GetType().Name, httpContext.TraceIdentifier);
        var (status, title) = Map(denial);
        return GatewayProblemWriter.WriteAsync(httpContext, status, title, Autheris.Api.Errors.ApiErrorCodes.StatusDefault(status), null, null, Autheris.Api.Errors.ApiProblems.ResolveTraceId(httpContext), ct);
    }
}
