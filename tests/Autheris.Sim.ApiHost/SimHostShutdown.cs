namespace Autheris.Sim.ApiHost;

/// <summary>
/// Single, bounded shutdown path of the sim host (REQ-LB-01..03). BCL types only, so the file is linked into the unit tests.
/// The caller starts the host stop exactly once (<see cref="Request"/> returns true for the first signal only); <see cref="RunAsync"/> then
/// waits for the stop, lets the entry-point thread dispose the host and falls back to an explicit dispose only after a timeout.
/// Exit codes: 0 clean, 4 shutdown exception, 5 shutdown timeout. No exception escapes <see cref="RunAsync"/> (SEC-LB-05, SEC-LB-06).
/// </summary>
public sealed class SimHostShutdown
{
    public const int ExitShutdownFailed = 4;
    public const int ExitShutdownTimeout = 5;

    private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requests;

    /// <summary>Completes when the first stop request arrived.</summary>
    public Task Requested => _requested.Task;

    /// <summary>Idempotent: true for the first request, false for every later one (a second signal must not restart anything).</summary>
    public bool Request()
    {
        if (Interlocked.Increment(ref _requests) != 1)
        {
            return false;
        }

        _requested.TrySetResult();
        return true;
    }

    /// <summary>Single-line "Type: Message" description. Used only for stderr, which the script prints through its canary scrub (SEC-LB-03).</summary>
    public static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}".ReplaceLineEndings(" ");

    public async Task<int> RunAsync(
        Func<CancellationToken, Task> waitStopped,
        Func<bool> hostDisposed,
        Func<Task> fallbackDispose,
        Action cleanup,
        TextWriter stderr,
        TimeSpan stopTimeout,
        TimeSpan disposeTimeout)
    {
        var code = 0;
        try
        {
            try
            {
                var timedOut = false;
                try
                {
                    using var cts = new CancellationTokenSource(stopTimeout);
                    await waitStopped(cts.Token).WaitAsync(stopTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    timedOut = true;
                }

                if (!timedOut)
                {
                    timedOut = !await WaitUntilAsync(hostDisposed, disposeTimeout).ConfigureAwait(false);
                }

                if (timedOut)
                {
                    code = ExitShutdownTimeout;
                    SafeWrite(stderr, "sim-api-host: shutdown timed out; disposing the host explicitly");
                    await fallbackDispose().WaitAsync(disposeTimeout, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                code = ExitShutdownFailed;
                SafeWrite(stderr, $"sim-api-host: shutdown failed: {Describe(ex)}");
            }
        }
        finally
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                code = ExitShutdownFailed;
                SafeWrite(stderr, $"sim-api-host: cleanup failed: {Describe(ex)}");
            }
        }

        return code;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>A failing stderr must never hide the exit code or skip the cleanup.</summary>
    private static void SafeWrite(TextWriter writer, string line)
    {
        try
        {
            writer.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // nothing else to do
        }
    }
}
