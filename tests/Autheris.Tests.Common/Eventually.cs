namespace Autheris.Tests.Common;

/// <summary>Polling helper for conditions that become true asynchronously.</summary>
public static class Eventually
{
    public static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(10);
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            try
            {
                await Task.Delay(interval, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return condition();
            }
        }

        return true;
    }
}
