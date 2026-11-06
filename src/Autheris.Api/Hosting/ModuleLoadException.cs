namespace Autheris.Api.Hosting;

using System;

/// <summary>
/// Exception thrown during startup when an external module fails trust, integrity, boundary, or security verification.
/// </summary>
public sealed class ModuleLoadException : Exception
{
    public ModuleLoadException(string moduleId, string reason, Exception? innerException = null)
        : base($"Module '{moduleId}' failed to load: {reason}", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ModuleId = moduleId;
        Reason = reason;
    }

    public string ModuleId { get; }

    public string Reason { get; }
}
