namespace Autheris.Api.Hosting.Warmup;

/// <summary>Phase of the best-effort warm-up. <see cref="Pending"/> is the initial value and maps to 503.</summary>
public enum WarmupPhase
{
    Pending = 0,
    Warming = 1,
    Ready = 2,
}

/// <summary>Live (uncached) view of the best-effort warm-up for <c>/health/ready</c>.</summary>
public interface IStartupWarmupState
{
    WarmupPhase Phase { get; }
}

/// <summary>Lock-free state holder (single volatile int).</summary>
public sealed class StartupWarmupState : IStartupWarmupState
{
    private int _phase;

    public StartupWarmupState(WarmupPhase initial = WarmupPhase.Pending) => _phase = (int)initial;

    public WarmupPhase Phase => (WarmupPhase)Volatile.Read(ref _phase);

    internal void Set(WarmupPhase phase) => Volatile.Write(ref _phase, (int)phase);
}
