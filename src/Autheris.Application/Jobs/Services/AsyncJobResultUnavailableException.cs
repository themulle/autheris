namespace Autheris.Application.Jobs.Services;

using System;

/// <summary>The result file of a job lives on another replica and the scratch directory is not shared (the endpoint answers HTTP 503).</summary>
public sealed class AsyncJobResultUnavailableException : Exception
{
    public const string Code = "ASYNC_JOB_RESULT_NODE_LOCAL";

    public AsyncJobResultUnavailableException()
        : base("The job result is stored on another gateway node. Retry on that node or configure a shared scratch directory.")
    {
    }

    public string ErrorCode => Code;
}
