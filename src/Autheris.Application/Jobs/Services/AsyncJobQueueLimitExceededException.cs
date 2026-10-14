namespace Autheris.Application.Jobs.Services;

using System;

/// <summary>A tenant already has the maximum number of queued and running async jobs (the endpoint answers HTTP 429).</summary>
public sealed class AsyncJobQueueLimitExceededException : Exception
{
    public AsyncJobQueueLimitExceededException(int limit)
        : base($"The tenant already has {limit} queued or running async jobs. Retry later.")
    {
        Limit = limit;
    }

    public int Limit { get; }
}
