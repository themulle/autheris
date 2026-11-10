namespace Autheris.Application.Compliance;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class GdprDisclosureReportService : IGdprDisclosureReportService
{
    private readonly IDataOwnershipRepository _ownershipRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly ITableMetadataRepository? _tableMetadataRepo;
    private readonly ILogger<GdprDisclosureReportService>? _logger;
    private readonly int _maxGdprAuditEvents;

    public GdprDisclosureReportService(
        IDataOwnershipRepository ownershipRepo,
        IAuditLogRepository auditRepo,
        ITableMetadataRepository? tableMetadataRepo = null,
        IOptions<GdprReportOptions>? gdprReportOptions = null,
        ILogger<GdprDisclosureReportService>? logger = null)
    {
        _ownershipRepo = ownershipRepo ?? throw new ArgumentNullException(nameof(ownershipRepo));
        _auditRepo = auditRepo ?? throw new ArgumentNullException(nameof(auditRepo));
        _tableMetadataRepo = tableMetadataRepo;
        _logger = logger;
        _maxGdprAuditEvents = gdprReportOptions?.Value?.MaxAuditEvents ?? new GdprReportOptions().MaxAuditEvents;
    }

    /// <summary>
    /// SEC M-15: Zugriffs- und Auswirkungsanalysen sind auf Data Owner/Delegierte der Tabelle,
    /// GovernanceAdmin und PrivacyAdmin beschränkt.
    /// </summary>
    private async Task<bool> IsAuthorizedForAccessAnalysisAsync(TableIdentifier table, CallerSecurityContext callerContext, CancellationToken ct)
    {
        if (callerContext.IsGovernanceAdmin ||
            callerContext.Roles.Any(r =>
                string.Equals(r, "GovernanceAdmin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r, "PrivacyAdmin", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(callerContext.UserSid.Value))
        {
            return false;
        }

        return await _ownershipRepo.IsAuthorizedApproverForTableAsync(table, callerContext.UserSid, ct).ConfigureAwait(false);
    }

    public async Task<GdprDisclosureReport> GetGdprDataDisclosureReportAsync(
        TableIdentifier? table,
        Sid? subjectSid,
        int timeWindowDays = 365,
        CallerSecurityContext? callerContext = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var activity = GatewayDiagnostics.Source.StartActivity("Lineage.GetGdprDisclosureReport");

        // MISC-11: the audit log carries no data-subject keys, so "who read rows about the subject" cannot be answered from it. The report is
        // honest table-level evidence: the readers of the table that holds the subject's data. Without a table the scope is unresolved.
        if (!table.HasValue)
        {
            throw new GdprSubjectScopeUnresolvedException();
        }

        // GHSA-qx32-576x-5cq2: analysis standing
        bool isAuthorized = callerContext == null ||
            await IsAuthorizedForAccessAnalysisAsync(table.Value, callerContext, ct).ConfigureAwait(false);

        if (callerContext != null && !isAuthorized)
        {
            if (!subjectSid.HasValue || !subjectSid.Value.Equals(callerContext.UserSid))
            {
                throw new UnauthorizedAccessException($"Caller '{callerContext.UserSid}' is not authorized to view audit disclosure reports for table '{table.Value}'.");
            }
        }

        var window = Math.Clamp(timeWindowDays, 1, 3650);
        var since = DateTimeOffset.UtcNow.AddDays(-window);
        var targetTableStr = table.Value.ToString();
        var incompletenessReasons = new List<string>();

        // 1. Query Audit Logs by table only (the subject is never used as the actor filter); one extra row detects a cap hit.
        IReadOnlyList<AuditLogEntry> auditLogs;
        if (_auditRepo != null)
        {
            auditLogs = await _auditRepo.QueryAuditLogsAsync(
                targetTable: targetTableStr,
                actorSid: null,
                since: since,
                limit: _maxGdprAuditEvents + 1,
                tenantId: callerContext?.TenantId,
                ct: ct).ConfigureAwait(false);
            if (auditLogs.Count > _maxGdprAuditEvents)
            {
                auditLogs = auditLogs.Take(_maxGdprAuditEvents).ToList();
                incompletenessReasons.Add($"The audit event cap of {_maxGdprAuditEvents} was reached; older recipients may be missing.");
            }
        }
        else
        {
            auditLogs = Array.Empty<AuditLogEntry>();
            incompletenessReasons.Add("The audit log is not available.");
        }

        var allowedQuery = auditLogs
            .Where(a => string.Equals(a.Decision, "ALLOW", StringComparison.OrdinalIgnoreCase));

        if (callerContext != null && !isAuthorized)
        {
            // GHSA-qx32-576x-5cq2: actor visibility
            allowedQuery = allowedQuery.Where(a => string.Equals(a.ActorSid.Value, callerContext.UserSid.Value, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            allowedQuery = allowedQuery.Where(a => subjectSid is not { } subject || !string.Equals(a.ActorSid.Value, subject.Value, StringComparison.OrdinalIgnoreCase));
        }

        var allowedLogs = allowedQuery.ToList();

        // 2. Determine Data Sensitivity Categories
        var sensitivityCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, MaskingRule>? maskingRules = null;

        if (table.HasValue && _tableMetadataRepo != null)
        {
            var meta = await _tableMetadataRepo.GetTableMetadataAsync(table.Value, ct).ConfigureAwait(false);
            if (meta != null)
            {
                maskingRules = meta.ColumnMaskingRules;
                var sens = meta.Table.Sensitivity?.ToUpperInvariant() ?? "NORMAL";
                if (sens.Contains("ART9") || sens.Contains("ARTICLE_9") || meta.Table.RequiresFourEyes)
                {
                    sensitivityCategories.Add("GDPR_ARTICLE_9 (Special Category: Health, Biometric, Genetic, Political or Religious Data)");
                }
                if (sens.Contains("PII"))
                {
                    sensitivityCategories.Add("PII (Personally Identifiable Information)");
                }
                if (sens.Contains("HIGH") || sens.Contains("CONFIDENTIAL"))
                {
                    sensitivityCategories.Add("CONFIDENTIAL_BUSINESS_DATA");
                }
            }
        }
        if (sensitivityCategories.Count == 0)
        {
            sensitivityCategories.Add("STANDARD_OPERATIONAL_DATA");
        }

        // 3. Aggregate Disclosed Recipients (Art. 15 Abs. 1 Bst. c DSGVO)
        var recipients = allowedLogs
            .GroupBy(l => l.ActorSid.Value)
            .Select(g =>
            {
                var recipientSid = g.Key;
                var firstAccess = g.Min(x => x.OccurredAt);
                var lastAccess = g.Max(x => x.OccurredAt);
                var totalQueries = g.Count();

                string recipientCategory = "InteractiveUser";
                if (recipientSid.Contains("svc", StringComparison.OrdinalIgnoreCase) ||
                    recipientSid.StartsWith("SP-", StringComparison.OrdinalIgnoreCase) ||
                    recipientSid.Contains("ServicePrincipal", StringComparison.OrdinalIgnoreCase) ||
                    recipientSid.Contains("batch", StringComparison.OrdinalIgnoreCase))
                {
                    recipientCategory = "ServicePrincipal";
                }
                else if (recipientSid.Contains("bi", StringComparison.OrdinalIgnoreCase) ||
                         recipientSid.Contains("dash", StringComparison.OrdinalIgnoreCase))
                {
                    recipientCategory = "DownstreamSystem";
                }

                // Extract accessed columns
                var accessedCols = g
                    .Select(x => x.TargetColumn)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(c => c!)
                    .ToList();

                // Masking rule applied
                string? appliedMask = null;
                if (maskingRules != null && accessedCols.Count > 0)
                {
                    foreach (var col in accessedCols)
                    {
                        if (maskingRules.TryGetValue(col, out var rule))
                        {
                            appliedMask = $"{col}: {rule.RuleType}";
                            break;
                        }
                    }
                }

                return new GdprRecipientAccessRecord(
                    recipientSid,
                    recipientCategory,
                    "Business Operations / Data Gateway Query Execution",
                    firstAccess,
                    lastAccess,
                    totalQueries,
                    accessedCols,
                    appliedMask);
            })
            .OrderByDescending(r => r.TotalQueries)
            .ToList();

        sw.Stop();

        return new GdprDisclosureReport(
            targetTableStr,
            subjectSid?.Value,
            DateTimeOffset.UtcNow,
            window,
            allowedLogs.Count,
            recipients,
            sensitivityCategories.ToList(),
            "Art. 15(1)(c) GDPR (DSGVO): Information about the recipients or categories of recipients to whom the personal data have been or will be disclosed.",
            IsComplete: incompletenessReasons.Count == 0,
            EvidenceLevel: "TableLevel",
            IncompletenessReasons: incompletenessReasons);
    }
}
