namespace Autheris.Application.State;

using System;

/// <summary>
/// Thrown by <see cref="IDistributedClusterStateProvider"/> implementations when the shared state store cannot be reached
/// or answered with an infrastructure failure. It is deliberately distinct from "key absent" (a successful read that
/// returns <c>default</c>): consumers that protect authentication, replay or budget decisions must treat this exception as
/// "unknown" and fail closed, never as "no value stored".
/// </summary>
public sealed class ClusterStateUnavailableException : Exception
{
    public ClusterStateUnavailableException()
        : base("The cluster state store is unavailable.")
    {
    }

    public ClusterStateUnavailableException(string message)
        : base(message)
    {
    }

    public ClusterStateUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public ClusterStateUnavailableException(string operation, string key, Exception? innerException)
        : base($"The cluster state store is unavailable (operation '{operation}').", innerException)
    {
        Operation = operation;
        Key = key;
    }

    /// <summary>The provider operation that failed (for example <c>get</c> or <c>set</c>), when known.</summary>
    public string? Operation { get; }

    /// <summary>The logical key or channel involved, when known. Never contains secret material by convention.</summary>
    public string? Key { get; }
}
