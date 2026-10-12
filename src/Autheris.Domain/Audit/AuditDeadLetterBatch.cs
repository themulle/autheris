namespace Autheris.Domain.Audit;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// AU-05: Record representing an audit batch buffered into the dead-letter queue after maximum retries.
/// </summary>
public sealed record AuditDeadLetterBatch(
    string Id,
    IReadOnlyList<AuditLogEntry> Entries,
    string ErrorMessage,
    DateTimeOffset FailedAt,
    string TenantId);
