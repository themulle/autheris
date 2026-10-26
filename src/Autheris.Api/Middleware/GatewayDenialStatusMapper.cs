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
    /// DT-A-2: the governed code of the two tenant-resolution denials, chosen by exception type only (never from exception data,
    /// SEC-PG-14). <c>null</c> for every other denial, which keeps the status-derived code.
    /// </summary>
    internal static string? SpecificCode(GatewaySecurityException exception) => exception switch
    {
        TenantUnresolvedException => Autheris.Api.Errors.ApiErrorCodes.TenantUnresolved,
        TenantClaimInvalidException => Autheris.Api.Errors.ApiErrorCodes.TenantClaimInvalid,
        _ => null
    };

    /// <summary>
    /// The WebSQL response of a denial: the status of <see cref="Map"/> with a generic message. Only the code and the
    /// exception type are logged, never the exception object: the inner exception of a key source failure may quote
    /// data values (C-8, SEC-11).
    /// </summary>
    internal static async Task WriteWebSqlResponseAsync(
        HttpContext httpContext, GatewaySecurityException denial, ILogger logger, string forbiddenMessage, CancellationToken ct)
    {
        logger.LogWarning(
            "WebSQL denied by the gateway ({ErrorCode}, {ExceptionType}). TraceId={TraceId}",
            denial.ErrorCode, denial.GetType().Name, httpContext.TraceIdentifier);
        var (status, title) = Map(denial);
        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await httpContext.Response.WriteAsJsonAsync(new
        {
            error = status switch
            {
                StatusCodes.Status401Unauthorized => "Unauthorized",
                StatusCodes.Status400BadRequest => "BadRequest",
                StatusCodes.Status503ServiceUnavailable => "ServiceUnavailable",
                _ => "Forbidden"
            },
            message = status == StatusCodes.Status403Forbidden ? forbiddenMessage : title,
            traceId = httpContext.TraceIdentifier
        }, ct);
    }
}
