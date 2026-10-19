namespace Autheris.Application.Common;

using System;
using Microsoft.Extensions.Logging;

/// <summary>
/// Failure logging for paths that handle governed or staged data (SEC-FED-DELTA-01). Exception messages and inner
/// exceptions can quote data values, so a line at Information or above carries the caller's constant template, its
/// arguments and the exception type name, never the exception object. The full exception is written at Debug only.
/// </summary>
public static class SafeLog
{
    /// <summary>Logs at <see cref="LogLevel.Warning"/>: <paramref name="template"/> plus the exception type name.</summary>
    public static void Warning(this ILogger logger, Exception? ex, string template, params object?[] args) =>
        Write(logger, LogLevel.Warning, ex, template, args);

    /// <summary>Logs at <see cref="LogLevel.Error"/>: <paramref name="template"/> plus the exception type name.</summary>
    public static void Error(this ILogger logger, Exception? ex, string template, params object?[] args) =>
        Write(logger, LogLevel.Error, ex, template, args);

    private static void Write(ILogger logger, LogLevel level, Exception? ex, string template, object?[] args)
    {
        var withType = new object?[args.Length + 1];
        args.CopyTo(withType, 0);
        withType[^1] = ex?.GetType().Name ?? "Unknown";
#pragma warning disable CA2254 // The template is a constant at every call site; only the type placeholder is appended here.
        logger.Log(level, template + " ({ExceptionType})", withType);
        logger.LogDebug(ex, template + " (full exception)", args);
#pragma warning restore CA2254
    }
}
