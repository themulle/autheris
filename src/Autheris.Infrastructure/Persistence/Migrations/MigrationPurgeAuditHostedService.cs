using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>
/// Writes a single fail-closed <c>MIGRATION_GRANTS_PURGED</c> audit record on startup when the D13 migration purge
/// generated unaudited report rows, recording per-table row counts and the archived ledger head digest.
/// </summary>
public sealed class MigrationPurgeAuditHostedService(
    MigrationPurgeStore purgeStore,
    IAuditWriter auditWriter,
    ILogger<MigrationPurgeAuditHostedService>? logger = null,
    TimeProvider? timeProvider = null,
    string stepName = MigrationPurgeStore.PermissionRequestsPurgeStep) : IHostedService
{
    private readonly MigrationPurgeStore _purgeStore = purgeStore ?? throw new ArgumentNullException(nameof(purgeStore));
    private readonly IAuditWriter _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
    private readonly ILogger<MigrationPurgeAuditHostedService>? _logger = logger;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly string _stepName = string.IsNullOrWhiteSpace(stepName) ? MigrationPurgeStore.PermissionRequestsPurgeStep : stepName.Trim();

    public Task StartAsync(CancellationToken cancellationToken) =>
        AuditPendingPurgesOnceAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<bool> AuditPendingPurgesOnceAsync(CancellationToken ct = default)
    {
        var reports = await _purgeStore.GetUnauditedPurgeReportsAsync(_stepName, ct).ConfigureAwait(false);
        if (reports.Count == 0)
        {
            return false;
        }

        var archivedHeads = await _purgeStore.GetArchivedHeadsAsync(_stepName, ct).ConfigureAwait(false);
        var headDigest = MigrationPurgeStore.ComputeArchivedHeadDigest(archivedHeads);
        var now = _time.GetUtcNow();

        var tableCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in reports)
        {
            tableCounts[row.TableName] = row.RowCount;
        }

        var detailsJson = JsonSerializer.Serialize(new
        {
            Step = _stepName,
            PurgedAt = reports.Min(r => r.PurgedAt).ToUniversalTime(),
            ArchivedHeadsCount = archivedHeads.Count,
            ArchivedHeadHash = headDigest,
            Tables = tableCounts
        });

        await _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = now,
            EventType = AuditEventTypes.MigrationGrantsPurged,
            ActorSid = new Sid("SYSTEM:MIGRATION"),
            TargetTable = "*",
            Decision = "PURGED",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = detailsJson,
            TenantId = TenantId.Platform
        }, ct).ConfigureAwait(false);

        await _purgeStore.MarkPurgeReportsAuditedAsync(_stepName, now, ct).ConfigureAwait(false);
        _logger?.LogInformation(
            "Recorded startup purge audit {EventType} for migration step {Step} across {TableCount} tables.",
            AuditEventTypes.MigrationGrantsPurged,
            _stepName,
            tableCounts.Count);
        return true;
    }
}
