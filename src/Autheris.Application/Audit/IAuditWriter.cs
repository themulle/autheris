using Autheris.Domain.Model;

namespace Autheris.Application.Audit;

/// <summary>
/// WP-ARC-A-06: the single place that decides what happens when an audit write fails. Replaces hand-written
/// <c>GetService&lt;IAuditLogRepository&gt;()</c> / <c>if (repo != null)</c> blocks in endpoints and services.
/// </summary>
public interface IAuditWriter
{
    /// <summary>Writes the record or throws <see cref="AuditUnavailableException"/>. Callers abort the operation (ALLOW and admin changes).</summary>
    Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default);

    /// <summary>Writes the record; failures are logged and counted (<c>autheris_audit_write_failed_total</c>) and never thrown.</summary>
    Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default);

    /// <summary>Dispatches by <see cref="AuditPolicyTable"/> for the entry's event type.</summary>
    Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default);
}
