namespace Autheris.Application.State;

using System;

/// <summary>
/// Thrown when a downstream store rejects a write because it already saw a newer fencing token for the resource,
/// meaning the writer lost its lease to a newer owner.
/// </summary>
public sealed class FencingTokenRejectedException : Exception
{
    public FencingTokenRejectedException(string resource, long token)
        : base($"The write to '{resource}' was rejected: fencing token {token} is stale.")
    {
        Resource = resource;
        Token = token;
    }

    public FencingTokenRejectedException()
        : base("The write was rejected: the fencing token is stale.")
    {
        Resource = string.Empty;
    }

    public FencingTokenRejectedException(string message)
        : base(message)
    {
        Resource = string.Empty;
    }

    public FencingTokenRejectedException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Resource = string.Empty;
    }

    public string Resource { get; }

    public long Token { get; }
}
