namespace Autheris.Application.Runtime.Jobs.Services;

using System;

/// <summary>The job queue no longer accepts work because the worker stopped (the endpoint answers HTTP 503).</summary>
public sealed class AsyncJobQueueStoppedException : Exception
{
    public AsyncJobQueueStoppedException()
        : base("The async job queue is not accepting jobs (the gateway is shutting down). Retry later.")
    {
    }
}
