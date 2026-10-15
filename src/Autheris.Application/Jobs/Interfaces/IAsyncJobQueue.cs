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

    /// <summary>
    /// Deletes result files in the scratch directory that no job refers to any more (crash leftovers): files of unknown
    /// jobs older than the maximum job timeout, and any file older than the retention period.
    /// </summary>
    ValueTask SweepOrphanedScratchFilesAsync(CancellationToken ct);

    /// <summary>
    /// Applies a cancel request that another node published for a job running here. Ignored unless tenant and requester
    /// match the local job entry.
    /// </summary>
    ValueTask ApplyCancelSignalAsync(AsyncJobCancelSignal signal, CancellationToken ct);

    /// <summary>Stops accepting new jobs: later submits are rejected. Called when the worker stops.</summary>
    void Complete();
}
