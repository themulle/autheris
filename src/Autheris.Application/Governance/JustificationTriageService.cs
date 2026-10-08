namespace Autheris.Application.Governance;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class JustificationTriageService : IJustificationTriageService
{
    private readonly IOpenJevClient _openJevClient;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly ILogger<JustificationTriageService> _logger;

    public JustificationTriageService(
        IOpenJevClient openJevClient,
        ITableMetadataRepository metadataRepo,
        IAuditLogRepository auditRepo,
        ILogger<JustificationTriageService> logger)
    {
        _openJevClient = openJevClient;
        _metadataRepo = metadataRepo;
        _auditRepo = auditRepo;
        _logger = logger;
    }

    public async Task<JustificationTriageResult> TriageJustificationAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string justificationText,
        CancellationToken ct = default)
    {
        // 1. OpenJev Klassifikation mit Injection-Schutz
        var rawResult = await _openJevClient.ClassifyJustificationAsync(tenant, userSid, table, justificationText, ct).ConfigureAwait(false);

        // 2. Tabellen-Metadaten prüfen (Prüfung auf explizites LOW_SENSITIVITY Opt-In)
        var metadata = await _metadataRepo.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        bool isLowSensitivityOptIn = metadata != null &&
            !metadata.Table.RequiresFourEyes &&
            (string.Equals(metadata.Table.Sensitivity, "LOW", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(metadata.Table.Sensitivity, "LOW_SENSITIVITY", StringComparison.OrdinalIgnoreCase));

        // 3. Strikte Triage-Matrix (Kein Auto-Grant außer bei LOW_SENSITIVITY Opt-In je Tabelle)
        var triageResult = EvaluateTriageDecision(rawResult.Category, rawResult.Confidence, isLowSensitivityOptIn);

        // 4. Audit & Alerting Logging
        if (triageResult.Category is JustificationCategory.Unjustified or JustificationCategory.SuspiciousExfiltration)
        {
            _logger.LogWarning("Security warning: Justification for {Table} by {UserSid} classified as {Category}",
                table, userSid, triageResult.Category);

            await _auditRepo.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenant,
                EventType = "JUSTIFICATION_SECURITY_ALERT",
                ActorSid = userSid,
                TargetTable = table.ToString(),
                Decision = "DENY",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    tenant = tenant.Value,
                    category = triageResult.Category.ToString(),
                    confidence = triageResult.Confidence,
                    rawOutput = triageResult.RawModelOutput
                })
            }, ct).ConfigureAwait(false);
        }
        else if (triageResult.AutoGrantEligible)
        {
            await _auditRepo.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenant,
                EventType = "AUTO_GRANT_LOW_SENSITIVITY",
                ActorSid = userSid,
                TargetTable = table.ToString(),
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    tenant = tenant.Value,
                    category = triageResult.Category.ToString(),
                    durationHours = triageResult.GrantedDuration?.TotalHours
                })
            }, ct).ConfigureAwait(false);
        }

        return triageResult;
    }

    public static JustificationTriageResult EvaluateTriageDecision(
        JustificationCategory category,
        double confidence,
        bool isLowSensitivityOptIn) => (category, confidence, isLowSensitivityOptIn) switch
    {
        (JustificationCategory.Unjustified or JustificationCategory.SuspiciousExfiltration, _, _) =>
            new JustificationTriageResult(category, confidence, "Denied by security classification", false, null),

        (JustificationCategory.LegitimateAudit or JustificationCategory.IncidentTriage, >= 0.98, true) =>
            new JustificationTriageResult(category, confidence, "Auto-grant low sensitivity", true, TimeSpan.FromHours(4)),

        _ =>
            new JustificationTriageResult(category, confidence, "Routed to human 4-eyes approval", false, null)
    };
}
