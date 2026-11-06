namespace Autheris.Modules.Abstractions;

using System;

/// <summary>
/// Host context services exposed to external modules during <see cref="IAutherisModule.ConfigureServices"/>.
/// </summary>
public interface IModuleHostContext
{
    /// <summary>
    /// Host time provider for deterministic time resolution.
    /// </summary>
    TimeProvider Time { get; }

    /// <summary>
    /// Semantic version of the running Autheris host binary.
    /// </summary>
    string HostVersion { get; }

    /// <summary>
    /// Factory that opens connections exclusively to the governance persistence store.
    /// </summary>
    IGovernanceConnectionFactory GovernanceConnections { get; }
}
