using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Passive in-memory <see cref="IAuditWriter"/> recording written entries in append order.
/// Optionally simulates write failures for fail-closed vs best-effort tests.
/// </summary>
public sealed class InMemoryAuditWriter(Exception? failure = null, bool throwOnAllWrites = false) : IAuditWriter
{
    private readonly object _gate = new();
    private readonly List<AuditLogEntry> _entries = [];

    public IReadOnlyList<AuditLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (failure is not null)
        {
            throw failure;
        }

        lock (_gate)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (failure is null)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        return Task.CompletedTask;
    }

    public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (throwOnAllWrites && failure is not null)
        {
            throw failure;
        }

        return AuditPolicyTable.For(entry.EventType) == AuditFailurePolicy.FailClosed
            ? RecordRequiredAsync(entry, ct)
            : RecordBestEffortAsync(entry, ct);
    }
}
