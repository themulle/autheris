namespace Autheris.Tests.Common;

/// <summary>Awaitable "never completes on its own" signals for test doubles that must block until cancelled.</summary>
public static class Hang
{
    /// <summary>Returns a task that stays pending until <paramref name="ct"/> is cancelled, then ends canceled (like an infinite delay).</summary>
    public static Task UntilCancelledAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.CanBeCanceled)
        {
            ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), tcs);
        }

        return tcs.Task;
    }
}
