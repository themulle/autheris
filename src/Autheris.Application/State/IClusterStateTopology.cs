namespace Autheris.Application.State;

/// <summary>How the registered <see cref="IDistributedClusterStateProvider"/> is backed.</summary>
public enum ClusterStateMode
{
    /// <summary>Process-local state (single node, development, tests).</summary>
    InMemory,

    /// <summary>Embedded Garnet server running inside this process: shared with nobody, not a cluster.</summary>
    Embedded,

    /// <summary>External Redis/Garnet reachable by every replica.</summary>
    External
}

/// <summary>
/// Describes whether the registered cluster state is shared by all gateway replicas. Consumers use it to decide whether
/// a local-only fallback is acceptable (<see cref="IsClusterWide"/> is false) or whether they must fail closed.
/// </summary>
public interface IClusterStateTopology
{
    ClusterStateMode Mode { get; }

    /// <summary>True only when the state store is external and therefore shared across replicas.</summary>
    bool IsClusterWide { get; }
}

public sealed class ClusterStateTopology : IClusterStateTopology
{
    public ClusterStateTopology(ClusterStateMode mode) => Mode = mode;

    public ClusterStateMode Mode { get; }

    public bool IsClusterWide => Mode == ClusterStateMode.External;
}
