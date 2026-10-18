namespace Autheris.Api.Hosting.Warmup;

/// <summary>PLAN-OPS-150 (D-150-7): how a warm-up step affects readiness.</summary>
public enum WarmupKind
{
    /// <summary>
    /// A failure or overrun is logged and counted and no longer blocks readiness after the global bound (D-150-3).
    /// The GraphQL executor is deliberately not a step: it is guarded by <see cref="IGraphQlExecutorGate"/> (A-150-1/-3).
    /// </summary>
    BestEffort,
}

/// <summary>
/// A compiled-in warm-up step run by <see cref="StartupWarmupService"/>. Steps must not resolve a principal, call a connector or
/// write audit, consent or governance data (SEC-OPS150-07).
/// </summary>
public interface IStartupWarmup
{
    /// <summary>Allow-listed step name (metric tag, log value, <c>Steps:&lt;Name&gt;</c> option key).</summary>
    string Name { get; }

    WarmupKind Kind { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
