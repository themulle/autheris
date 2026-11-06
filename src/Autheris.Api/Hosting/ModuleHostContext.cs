namespace Autheris.Api.Hosting;

using System;
using Autheris.Modules.Abstractions;

/// <summary>
/// Default implementation of <see cref="IModuleHostContext"/> passed to external modules during service registration.
/// </summary>
public sealed class ModuleHostContext : IModuleHostContext
{
    public ModuleHostContext(
        TimeProvider time,
        string hostVersion,
        IGovernanceConnectionFactory governanceConnections)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVersion);
        ArgumentNullException.ThrowIfNull(governanceConnections);

        Time = time;
        HostVersion = hostVersion;
        GovernanceConnections = governanceConnections;
    }

    public TimeProvider Time { get; }

    public string HostVersion { get; }

    public IGovernanceConnectionFactory GovernanceConnections { get; }
}
