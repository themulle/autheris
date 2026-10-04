namespace Autheris.Application.Interfaces;

public interface ITrafficDrainController
{
    bool IsDraining { get; }
    int ActiveQueryCount { get; }
    void InitiateGracefulShutdown();
    void MarkCompleted();
    IDisposable TrackQuery();
    Task WaitForCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
