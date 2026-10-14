namespace Autheris.Application.Jobs.Services;

using System;
using Autheris.Application.Sql;
using Autheris.Domain.Options;

/// <summary>
/// SR-H-05: the client-supplied MaxRows / Timeout of an async job only lower the configured caps, never raise them.
/// </summary>
internal static class AsyncJobLimits
{
    /// <summary>Row limit of one job: min(request, WebSql / AsyncJob channel limit, AsyncJobs.MaxRowsHardCap), at least 1.</summary>
    public static SqlRowLimit ResolveRowLimit(GatewayOptions? options, int requestedMaxRows)
    {
        var webSql = options?.WebSql ?? new WebSqlOptions();
        var hardCap = Math.Max(1, (options?.AsyncJobs ?? new AsyncJobOptions()).MaxRowsHardCap);
        var channel = SqlRowLimit.For(webSql, options?.RowLimits?.AsyncJob);

        // Fail closed: a non-positive request is one row, not "unlimited".
        var requested = Math.Max(1, (long)requestedMaxRows);
        var allowed = Math.Min(requested, hardCap);
        if (channel.MaxAllowedRows > 0)
        {
            allowed = Math.Min(allowed, channel.MaxAllowedRows);
        }

        // The job's own cap is also its default: a statement without LIMIT returns up to the requested rows.
        return new SqlRowLimit(allowed, allowed);
    }

    /// <summary>Runtime of one job: the requested timeout (if any) limited to AsyncJobs.MaxTimeoutSeconds; always armed.</summary>
    public static TimeSpan ResolveTimeout(GatewayOptions? options, TimeSpan? requested)
    {
        var max = TimeSpan.FromSeconds(Math.Max(1, (options?.AsyncJobs ?? new AsyncJobOptions()).MaxTimeoutSeconds));
        return requested is { } value && value > TimeSpan.Zero && value < max ? value : max;
    }
}
