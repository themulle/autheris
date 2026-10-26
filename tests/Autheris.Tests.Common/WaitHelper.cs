namespace Autheris.Tests.Common;

/// <summary>Polling and hang-guard helpers that replace wall-clock budgets and fixed delays in tests.
/// The boolean-returning variant of <see cref="WaitUntil(Func{bool}, TimeSpan, string?, TimeSpan?)"/> is <see cref="Eventually.WaitAsync"/>.</summary>
public static class WaitHelper
{
    /// <summary>Generous upper bound that only guards against a hang; never a performance budget.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>Waits until <paramref name="predicate"/> is true (delegates to <see cref="Eventually.WaitAsync"/>); throws <see cref="TimeoutException"/> otherwise.</summary>
    public static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout, string? because = null, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (!await Eventually.WaitAsync(predicate, timeout, pollInterval).ConfigureAwait(false))
        {
            throw TimeoutFailure(timeout, because);
        }
    }

    /// <summary>Async-predicate variant; the polling loop differs from <see cref="Eventually.WaitAsync"/> only because the predicate awaits.</summary>
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

                throw TimeoutFailure(timeout, because);
            }
        }
    }

    /// <summary>Runs synchronous work on the thread pool and fails (instead of hanging the run) after <see cref="HangGuard"/>.
    /// A genuinely hung work item cannot be aborted: its thread-pool thread keeps running after the test has failed.</summary>
    public static Task<T> RunGuarded<T>(Func<T> work) => Task.Run(work).WaitAsync(HangGuard);

    /// <inheritdoc cref="RunGuarded{T}(Func{T})"/>
    public static Task RunGuarded(Action work) => Task.Run(work).WaitAsync(HangGuard);

    private static TimeoutException TimeoutFailure(TimeSpan timeout, string? because) =>
        new($"Condition was not met within {timeout}." + (because is null ? string.Empty : " " + because));
}
