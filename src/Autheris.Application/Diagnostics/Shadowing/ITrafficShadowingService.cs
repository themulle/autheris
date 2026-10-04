namespace Autheris.Application.Diagnostics.Shadowing;

/// <summary>
/// F-OPS-01: Service contract for AST-Aware Production Traffic Shadowing and Dark Replay.
/// </summary>
public interface ITrafficShadowingService
{
    bool IsEnabled { get; }
    bool ShouldSample();
    bool EnqueueShadowRequest(ShadowRequest request);
    long EnqueuedRequestsCount { get; }
    long DroppedRequestsCount { get; }
    long ReplayedRequestsCount { get; }
}
