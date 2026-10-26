namespace Autheris.Tests.Common;

/// <summary>Polling and hang-guard helpers that replace wall-clock budgets and fixed delays in tests.</summary>
public static class WaitHelper
{
    /// <summary>Generous upper bound that only guards against a hang; never a performance budget.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    public static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout, string? because = null, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        await WaitUntil(() => Task.FromResult(predicate()), timeout, because, pollInterval).ConfigureAwait(false);
    }

    public static async Task WaitUntil(Func<Task<bool>> predicate, TimeSpan timeout, string? because = null, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(10);
        using var cts = new CancellationTokenSource(timeout);
        while (!await predicate().ConfigureAwait(false))
        {
            try
            {
                await Task.Delay(interval, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (await predicate().ConfigureAwait(false))
                {
                    return;
                }

                throw new TimeoutException($"Condition was not met within {timeout}." + (because is null ? string.Empty : " " + because));
            }
        }
    }

    /// <summary>Runs synchronous work on the thread pool and fails (instead of hanging the run) after <see cref="HangGuard"/>.</summary>
    public static Task<T> RunGuarded<T>(Func<T> work) => Task.Run(work).WaitAsync(HangGuard);

    public static Task RunGuarded(Action work) => Task.Run(work).WaitAsync(HangGuard);
}
