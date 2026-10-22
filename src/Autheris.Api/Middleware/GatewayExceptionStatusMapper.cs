namespace Autheris.Api.Middleware;

using System;
using Autheris.Application.Common;
using Autheris.Application.Sql.Errors;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;

internal static class GatewayExceptionStatusMapper
{
    internal const int ClientClosedRequest = 499;

    public static int Resolve(Exception ex)
    {
        if (ex == null)
        {
            return StatusCodes.Status500InternalServerError;
        }

        try
        {
            if (SqlCompilerErrorMapper.TryMap(ex, out var mapped))
            {
                return mapped.Status;
            }

            if (ex is GatewayThrottledException)
            {
                return StatusCodes.Status429TooManyRequests;
            }

            return MapStatus(ex);
        }
        catch
        {
            return StatusCodes.Status500InternalServerError;
        }
    }

    internal static int MapStatus(Exception exception) => exception switch
    {
        GatewayInvalidQueryException => StatusCodes.Status400BadRequest,
        GatewayThrottledException => StatusCodes.Status429TooManyRequests,
        Autheris.Application.Audit.AuditUnavailableException => StatusCodes.Status503ServiceUnavailable,
        GatewayNotImplementedException => StatusCodes.Status501NotImplemented,
        GatewayUnsupportedColumnTypeException => StatusCodes.Status501NotImplemented,
        NotSupportedException => StatusCodes.Status501NotImplemented,
        Antlr4.Runtime.Misc.ParseCanceledException => StatusCodes.Status400BadRequest,
        Antlr4.Runtime.RecognitionException => StatusCodes.Status400BadRequest,
        Exception e when e.InnerException is Antlr4.Runtime.Misc.ParseCanceledException or Antlr4.Runtime.RecognitionException =>
            StatusCodes.Status400BadRequest,
        GatewaySecurityException denial => GatewayDenialStatusMapper.Map(denial).Status,
        _ => DataAccessErrorClassifier.Classify(exception) switch
        {
            DataAccessErrorKind.Timeout => StatusCodes.Status504GatewayTimeout,
            DataAccessErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        }
    };
}
