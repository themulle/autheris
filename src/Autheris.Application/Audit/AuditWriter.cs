using Autheris.Application.Interfaces;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Audit;

/// <summary>Default <see cref="IAuditWriter"/> over <see cref="IAuditLogRepository"/>; constructor-injected and non-optional.</summary>
public sealed class AuditWriter : IAuditWriter
{
    private readonly IAuditLogRepository _repository;
    private readonly ILogger<AuditWriter> _logger;

    public AuditWriter(IAuditLogRepository repository, ILogger<AuditWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _logger = logger;
    }

    public async Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            await _repository.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AuditUnavailableException)
        {
            GatewayDiagnostics.AuditWriteFailedCounter.Add(1, new KeyValuePair<string, object?>("tier", "required"));
            throw;
        }
        catch (Exception ex)
        {
            GatewayDiagnostics.AuditWriteFailedCounter.Add(1, new KeyValuePair<string, object?>("tier", "required"));
            _logger.LogError(ex, "Required audit write failed for event {EventType}; failing closed.", entry.EventType);
            throw new AuditUnavailableException("The audit store is unavailable (fail-closed).", ex);
        }
    }

    public async Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            await _repository.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            GatewayDiagnostics.AuditWriteFailedCounter.Add(1, new KeyValuePair<string, object?>("tier", "best_effort"));
            _logger.LogWarning(ex, "Best-effort audit write failed for event {EventType}.", entry.EventType);
        }
    }

    public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return AuditPolicyTable.For(entry.EventType) == AuditFailurePolicy.FailClosed
            ? RecordRequiredAsync(entry, ct)
            : RecordBestEffortAsync(entry, ct);
    }
}
