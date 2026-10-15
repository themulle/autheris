using Autheris.Application.Auditing;
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
    private readonly AuditGroupCommitSink? _groupCommitSink;
    private readonly ICallerContextProvider? _callerContextProvider;

    /// <param name="groupCommitSink">
    /// WP-PF-04: when present, fail-closed (required / Tier-A) writes of concurrent requests are coalesced into one transaction, one
    /// chain-tail read and one anchor per batch. Every caller still awaits the durable commit and anchor of its own batch.
    /// </param>
    /// <param name="callerContextProvider">
    /// Resolves the ambient caller context on the REQUEST thread before the entry is handed to the sink's drain loop (the ambient
    /// context is not available there). Only used together with <paramref name="groupCommitSink"/>.
    /// </param>
    public AuditWriter(
        IAuditLogRepository repository,
        ILogger<AuditWriter> logger,
        AuditGroupCommitSink? groupCommitSink = null,
        ICallerContextProvider? callerContextProvider = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _logger = logger;
        _groupCommitSink = groupCommitSink;
        _callerContextProvider = callerContextProvider;
    }

    public async Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            if (_groupCommitSink != null)
            {
                if (entry.Caller == null && _callerContextProvider != null)
                {
                    entry.Caller = _callerContextProvider.ResolveCallerContext();
                }

                await _groupCommitSink.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
            }
            else
            {
                await _repository.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
