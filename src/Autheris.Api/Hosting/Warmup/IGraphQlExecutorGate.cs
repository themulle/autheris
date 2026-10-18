namespace Autheris.Api.Hosting.Warmup;

/// <summary>State of the required GraphQL executor gate. <see cref="Pending"/> and any unknown value map to 503 (SEC-OPS150-01).</summary>
public enum ExecutorGateState
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
}

/// <summary>
/// The required GraphQL executor gate consulted by <c>/health/ready</c> (PLAN-OPS-150 A-150-1..3). Independent of
/// <see cref="StartupWarmupOptions"/>: no switch or timeout can open it.
/// </summary>
public interface IGraphQlExecutorGate
{
    ExecutorGateState State { get; }
}
