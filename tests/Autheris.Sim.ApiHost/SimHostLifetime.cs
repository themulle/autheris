using Microsoft.Extensions.Hosting;

namespace Autheris.Sim.ApiHost;

/// <summary>
/// No-op host lifetime that replaces <c>ConsoleLifetime</c> (REQ-LB-01): <c>SimApiHostMain</c> owns the SIGTERM/SIGINT handling, so exactly one
/// component starts the host stop. It never binds or rebinds anything (SEC-LB-01).
/// </summary>
internal sealed class SimHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
