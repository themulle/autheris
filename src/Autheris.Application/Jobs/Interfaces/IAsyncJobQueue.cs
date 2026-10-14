namespace Autheris.Application.Jobs.Interfaces;

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Jobs.Services;
using Autheris.Domain.Model;

/// <summary>
/// The work-queue side of the async job engine, consumed by the background worker. Keeps the worker independent of the
/// concrete manager implementation.
/// </summary>
public interface IAsyncJobQueue
{
    /// <summary>Reader of the pending execution items.</summary>
    ChannelReader<AsyncJobExecutionItem> Reader { get; }

    /// <summary>Absolute directory result files are written to.</summary>
    string ScratchDirectory { get; }

    /// <summary>
    /// Records the next state of a job. Returns false when the job is already in a terminal state, which then stays
    /// terminal (the caller must discard its result).
    /// </summary>
    ValueTask<bool> TryTransitionAsync(AsyncJobDescriptor next, CancellationToken ct);
}
