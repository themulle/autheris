namespace Autheris.Application.Policy.Exceptions;

using System;

/// <summary>
/// Thrown when neither an epoch-validated cache entry nor the authoritative database is available
/// to resolve access profiles for a subject. Callers must fail closed (Deny).
/// </summary>
public sealed class AccessProfileSourceUnavailableException : Exception
{
    public AccessProfileSourceUnavailableException(string message) : base(message)
    {
    }

    public AccessProfileSourceUnavailableException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
