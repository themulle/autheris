namespace Autheris.Api.Hosting;

using System;
using Autheris.Modules.Abstractions;

/// <summary>
/// Default implementation of <see cref="IModuleHostContext"/> passed to external modules during service registration.
/// </summary>
public sealed class ModuleHostContext : IModuleHostContext
{
    public ModuleHostContext(TimeProvider time, string hostVersion)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVersion);

        Time = time;
        HostVersion = hostVersion;
    }

    public TimeProvider Time { get; }

    public string HostVersion { get; }
}
