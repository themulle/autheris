namespace Autheris.Infrastructure.Telemetry;

using System;
using Autheris.Domain.Diagnostics;

/// <summary>
/// Safe exception wrapper preserving type name and call stack while value-redacting the message.
/// Enforces SEC-OT06-06.
/// </summary>
public sealed class RedactedException : Exception
{
    private readonly string _stackTrace;
    private readonly string _originalTypeName;

    public RedactedException(Exception original)
        : base(TelemetryExceptionPolicy.SafeMessage(original))
    {
        ArgumentNullException.ThrowIfNull(original);
        _originalTypeName = original.GetType().FullName ?? original.GetType().Name;
        _stackTrace = original.StackTrace ?? string.Empty;
    }

    /// <summary>
    /// Gets the full type name of the original underlying exception.
    /// </summary>
    public string OriginalTypeName => _originalTypeName;

    /// <inheritdoc />
    public override string? StackTrace => _stackTrace;
}
