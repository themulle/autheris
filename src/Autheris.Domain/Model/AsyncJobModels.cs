namespace Autheris.Domain.Model;

using System;

public enum AsyncJobState
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

public sealed record AsyncQueryJobRequest(
    string Query,
    string Format = "json",
    int MaxRows = 100_000,
    TimeSpan? Timeout = null);

public sealed record AsyncJobDescriptor(
    string JobId,
    string TenantId,
    string SubmittedByUserId,
    string Query,
    string Format,
    AsyncJobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? ResultFilePath = null,
    long? RowsProduced = null,
    long? BytesProduced = null,
    string? ErrorMessage = null);

public sealed record AsyncJobStatusResponse(
    string JobId,
    AsyncJobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long? RowsProduced,
    long? BytesProduced,
    string? ErrorMessage,
    string? ResultDownloadUrl);
