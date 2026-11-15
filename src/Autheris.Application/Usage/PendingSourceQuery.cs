namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>Query execution awaiting asynchronous source-side compute and cost collection.</summary>
public sealed record PendingSourceQuery(
    TenantId Tenant,
    string DataSource,
    string SourceQueryId,
    DateOnly Day,
    byte[] SeriesHash,
    UsageSeriesKey SeriesKey,
    IReadOnlyList<string> GrantIds,
    DateTimeOffset EnqueuedAt,
    int AttemptCount = 0,
    DateTimeOffset? NextAttemptAt = null);
