namespace Autheris.Application.Jobs.Services;

using System;
using Autheris.Domain.Model;

/// <summary>
/// The part of an async job that is shared across replicas through the cluster state. It deliberately holds neither the
/// SQL text nor an absolute file path: the result is addressed by file name only and resolved against the scratch
/// directory of the node that serves it.
/// </summary>
public sealed record AsyncJobClusterRecord(
    string JobId,
    string TenantId,
    string OwnerSid,
    string Format,
    AsyncJobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long? RowsProduced,
    long? BytesProduced,
    string? ErrorMessage,
    string? ResultFileName,
    string OwnerNodeId);
