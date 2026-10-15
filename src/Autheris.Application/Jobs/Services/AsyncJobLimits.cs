namespace Autheris.Application.Jobs.Services;

using System;
using Autheris.Application.Sql;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Server-side bounds of an async query job (SR-H-05). The client-supplied MaxRows and Timeout are only ever narrowed,
/// never trusted: the row cap derives from the AsyncJob channel / WebSQL limits, the timeout from AsyncJobs options.
/// </summary>
internal static class AsyncJobLimits
{
    /// <summary>The row limit of the job: client MaxRows clamped to [1, configured MaxAllowedRows].</summary>
    public static SqlRowLimit ResolveRowLimit(AsyncQueryJobRequest request, GatewayOptions? options)
    {
        var configured = SqlRowLimit.For(options?.WebSql ?? new WebSqlOptions(), options?.RowLimits?.AsyncJob);
        var cap = configured.MaxAllowedRows > 0 ? configured.MaxAllowedRows : long.MaxValue;
        var rows = Math.Min(Math.Max(1L, request.MaxRows), cap);
        return new SqlRowLimit(rows, rows);
    }

    /// <summary>The job timeout: the default when absent or non-positive, otherwise clamped to the configured maximum.</summary>
    public static TimeSpan ResolveTimeout(AsyncQueryJobRequest request, GatewayOptions? options, AsyncJobOptions? jobOptions = null)
    {
        var jobs = jobOptions ?? options?.AsyncJobs ?? new AsyncJobOptions();
        var max = TimeSpan.FromSeconds(Math.Max(1, jobs.MaxTimeoutSeconds));
        var def = TimeSpan.FromSeconds(Math.Max(1, jobs.DefaultTimeoutSeconds));
        var requested = request.Timeout is { } t && t > TimeSpan.Zero ? t : def;
        return requested > max ? max : requested;
    }

    /// <summary>The request with its row count and timeout replaced by the server-enforced values.</summary>
    public static AsyncQueryJobRequest Normalize(AsyncQueryJobRequest request, GatewayOptions? options, AsyncJobOptions? jobOptions = null)
    {
        var limit = ResolveRowLimit(request, options);
        var rows = (int)Math.Min(limit.DefaultMaxRows, int.MaxValue);
        return request with { MaxRows = rows, Timeout = ResolveTimeout(request, options, jobOptions) };
    }
}
