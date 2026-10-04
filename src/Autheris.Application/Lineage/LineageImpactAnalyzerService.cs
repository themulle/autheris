namespace Autheris.Application.Lineage;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class LineageImpactAnalyzerService : ILineageImpactAnalyzerService
{
    private readonly IConsentRepository _consentRepo;
    private readonly IDataOwnershipRepository _ownershipRepo;
    private readonly ILineageGraphStore _graphStore;
    private readonly IAuditLogRepository? _auditRepo;
    private readonly ITableMetadataRepository? _tableMetadataRepo;
    private readonly ILogger<LineageImpactAnalyzerService> _logger;

    public LineageImpactAnalyzerService(
        IConsentRepository consentRepo,
        IDataOwnershipRepository ownershipRepo,
        ILineageGraphStore graphStore,
        IAuditLogRepository auditRepo,
        ITableMetadataRepository tableMetadataRepo,
        ILogger<LineageImpactAnalyzerService> logger)
    {
        _consentRepo = consentRepo ?? throw new ArgumentNullException(nameof(consentRepo));
        _ownershipRepo = ownershipRepo ?? throw new ArgumentNullException(nameof(ownershipRepo));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _auditRepo = auditRepo;
        _tableMetadataRepo = tableMetadataRepo;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public LineageImpactAnalyzerService(
        IConsentRepository consentRepo,
        IDataOwnershipRepository ownershipRepo,
        ILineageGraphStore graphStore,
        ILogger<LineageImpactAnalyzerService> logger)
        : this(consentRepo, ownershipRepo, graphStore, null!, null!, logger)
    {
    }

    public async Task<ConsentRevocationImpactReport> CalculateConsentRevocationImpactAsync(
        TenantId tenant,
        Guid consentId,
        CallerSecurityContext callerContext,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var activity = GatewayDiagnostics.Source.StartActivity("Lineage.Traverse");

        ArgumentNullException.ThrowIfNull(callerContext);

        var consent = await _consentRepo.GetConsentByIdAsync(consentId, ct).ConfigureAwait(false);
        // BOLA / Cross-tenant protection: reject if consent belongs to a different tenant
        // SEC M-15: Keine Ausnahme mehr für LegacySingleTenant-Consents (waren mandantenübergreifend sichtbar).
        if (consent == null ||
            (consent.TenantId != tenant &&
             !callerContext.IsClusterAdmin))
        {
            throw new KeyNotFoundException($"Consent mit ID '{consentId}' nicht gefunden.");
        }

        // SEC M-15: Nur Owner/Delegierte der Tabelle, GovernanceAdmin und PrivacyAdmin dürfen die Auswirkungsanalyse sehen.
        if (!await IsAuthorizedForAccessAnalysisAsync(consent.TableIdentifier, callerContext, ct).ConfigureAwait(false))
        {
            throw new GatewayForbiddenException("Auswirkungsanalysen erfordern Data-Owner-, GovernanceAdmin- oder PrivacyAdmin-Rechte.");
        }

        var rootTableId = consent.TableIdentifier.ToString();
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.Traverse", "tenant.id", tenant.Value);
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.Traverse", "root.node_id", rootTableId);

        // Zero-Trust Spaltenautorisierung für ownerEmail:
        // Nur GovernanceAdmin, ClusterAdmin oder der registrierte Data Owner der betroffenen Tabelle dürfen E-Mails sehen
        bool canViewEmail = callerContext.IsGovernanceAdmin || callerContext.IsClusterAdmin;
        if (!canViewEmail)
        {
            canViewEmail = await _ownershipRepo.IsAuthorizedApproverForTableAsync(consent.TableIdentifier, callerContext.UserSid, ct).ConfigureAwait(false);
        }

        var queue = new Queue<(string CurrentId, LineagePathNode Path)>(capacity: 64);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedMap = new Dictionary<string, AffectedEntity>(StringComparer.OrdinalIgnoreCase);
        bool containsCycles = false;

        queue.Enqueue((rootTableId, new LineagePathNode(rootTableId, null)));
        visited.Add(rootTableId);

        while (queue.Count > 0)
        {
            var (currentId, path) = queue.Dequeue();
            var node = _graphStore.GetNode(currentId);
            if (node == null || node.DownstreamNodeIds == null) continue;

            foreach (var downstreamId in node.DownstreamNodeIds)
            {
                if (path.Contains(downstreamId))
                {
                    // Echter Zyklus! downstreamId liegt auf dem Pfad von der Wurzel zu diesem Knoten
                    containsCycles = true;
                    _logger.LogWarning("Lineage Zyklus erkannt bei Knoten: {NodeId} -> {DownstreamId}", currentId, downstreamId);

                    var cyclicNode = _graphStore.GetNode(downstreamId);
                    if (cyclicNode != null)
                    {
                        affectedMap[downstreamId] = new AffectedEntity(
                            cyclicNode.Id,
                            cyclicNode.Name,
                            cyclicNode.Type,
                            cyclicNode.OwnerTeam,
                            canViewEmail ? cyclicNode.OwnerEmail : null,
                            CyclicReferenceDetected: true);
                    }
                    continue;
                }

                if (!visited.Add(downstreamId))
                {
                    // Konvergierender Pfad in einem DAG (z.B. Diamant-Graph) - bereits traversiert, kein Zyklus
                    continue;
                }

                var downstreamNode = _graphStore.GetNode(downstreamId);
                if (downstreamNode != null)
                {
                    affectedMap[downstreamId] = new AffectedEntity(
                        downstreamNode.Id,
                        downstreamNode.Name,
                        downstreamNode.Type,
                        downstreamNode.OwnerTeam,
                        canViewEmail ? downstreamNode.OwnerEmail : null,
                        CyclicReferenceDetected: false);

                    queue.Enqueue((downstreamId, new LineagePathNode(downstreamId, path)));
                }
            }
        }

        var affectedList = new List<AffectedEntity>(affectedMap.Values);

        // Severity-Berechnung:
        // HIGH wenn Dashboards oder Pipelines betroffen sind
        // MEDIUM bei ExternalService
        // LOW bei reinen Tabellen/Spalten
        string severity = "LOW";
        foreach (var entity in affectedList)
        {
            if (entity.Type is LineageNodeType.Dashboard or LineageNodeType.Pipeline)
            {
                severity = "HIGH";
                break;
            }
            if (entity.Type == LineageNodeType.ExternalService)
            {
                severity = "MEDIUM";
            }
        }

        sw.Stop();
        GatewayDiagnostics.LineageTraversalDuration.Record(sw.Elapsed.TotalMilliseconds);
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.Traverse", "nodes.visited", visited.Count);
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.Traverse", "cycle.detected", containsCycles);

        return new ConsentRevocationImpactReport(
            severity,
            affectedList.Count,
            affectedList,
            containsCycles);
    }

    public async Task<TableConsumersReport> GetTableConsumersAsync(
        TableIdentifier table,
        int timeWindowDays = 30,
        CallerSecurityContext? callerContext = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var activity = GatewayDiagnostics.Source.StartActivity("Lineage.GetTableConsumers");
        var rootTableId = table.ToString();
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.GetTableConsumers", "table", rootTableId);

        // Zero-Trust caller authorization for ownerEmail:
        bool canViewEmail = false;
        if (callerContext != null)
        {
            canViewEmail = callerContext.IsGovernanceAdmin || callerContext.IsClusterAdmin;
            if (!canViewEmail)
            {
                canViewEmail = await _ownershipRepo.IsAuthorizedApproverForTableAsync(table, callerContext.UserSid, ct).ConfigureAwait(false);
            }
        }

        // 1. Static Downstream Graph Lineage Traversal (BFS)
        var queue = new Queue<(string CurrentId, int Distance)>(capacity: 32);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downstreamConsumers = new List<DownstreamConsumerEntity>();

        queue.Enqueue((rootTableId, 0));
        visited.Add(rootTableId);

        while (queue.Count > 0)
        {
            var (currentId, distance) = queue.Dequeue();
            var node = _graphStore.GetNode(currentId);
            if (node?.DownstreamNodeIds == null) continue;

            foreach (var downstreamId in node.DownstreamNodeIds)
            {
                if (visited.Add(downstreamId))
                {
                    var childNode = _graphStore.GetNode(downstreamId);
                    if (childNode != null)
                    {
                        downstreamConsumers.Add(new DownstreamConsumerEntity(
                            childNode.Id,
                            childNode.Name,
                            childNode.Type,
                            childNode.OwnerTeam,
                            canViewEmail ? childNode.OwnerEmail : null,
                            distance + 1,
                            currentId));

                        queue.Enqueue((downstreamId, distance + 1));
                    }
                }
            }
        }

        // SEC M-15: Das Zugriffsprotokoll (Actor-SIDs, Zugriffszahlen, Zeiträume) sehen nur Owner/Delegierte,
        // GovernanceAdmin und PrivacyAdmin. Andere Aufrufer erhalten nur aggregierte Kennzahlen.
        bool canViewRuntimeConsumers = callerContext != null &&
            await IsAuthorizedForAccessAnalysisAsync(table, callerContext, ct).ConfigureAwait(false);

        // 2. Operational Runtime Consumers from Audit Logs
        var runtimeConsumers = new List<RuntimeConsumerSummary>();
        DateTimeOffset? lastAccessedAt = null;

        if (_auditRepo != null)
        {
            var window = Math.Clamp(timeWindowDays, 1, 3650);
            var since = DateTimeOffset.UtcNow.AddDays(-window);
            var auditEntries = await _auditRepo.QueryAuditLogsAsync(
                targetTable: rootTableId,
                since: since,
                limit: 5000,
                tenantId: callerContext?.TenantId,
                ct: ct).ConfigureAwait(false);

            var allowedEntries = auditEntries
                .Where(e => string.Equals(e.Decision, "ALLOW", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (allowedEntries.Count > 0)
            {
                lastAccessedAt = allowedEntries.Max(e => e.OccurredAt);

                runtimeConsumers = allowedEntries
                    .GroupBy(e => e.ActorSid.Value)
                    .Select(g =>
                    {
                        var actorSidStr = g.Key;
                        var count = g.Count();
                        var firstSeen = g.Min(x => x.OccurredAt);
                        var lastSeen = g.Max(x => x.OccurredAt);

                        string clientType = "InteractiveUser";
                        if (actorSidStr.Contains("svc", StringComparison.OrdinalIgnoreCase) ||
                            actorSidStr.StartsWith("SP-", StringComparison.OrdinalIgnoreCase) ||
                            actorSidStr.Contains("ServicePrincipal", StringComparison.OrdinalIgnoreCase) ||
                            actorSidStr.Contains("batch", StringComparison.OrdinalIgnoreCase))
                        {
                            clientType = "ServicePrincipal";
                        }
                        else if (actorSidStr.Contains("dash", StringComparison.OrdinalIgnoreCase) ||
                                 actorSidStr.Contains("bi", StringComparison.OrdinalIgnoreCase))
                        {
                            clientType = "DownstreamSystem";
                        }

                        return new RuntimeConsumerSummary(
                            actorSidStr,
                            count,
                            firstSeen,
                            lastSeen,
                            clientType);
                    })
                    .OrderByDescending(r => r.QueryCount)
                    .ToList();
            }
        }

        // 3. Schema Change Risk Assessment
        bool hasDashboards = downstreamConsumers.Any(d => d.Type == LineageNodeType.Dashboard);
        bool hasPipelines = downstreamConsumers.Any(d => d.Type == LineageNodeType.Pipeline);
        bool hasExternalServices = downstreamConsumers.Any(d => d.Type == LineageNodeType.ExternalService);
        int activeReadersCount = runtimeConsumers.Count;
        int totalAuditReads = runtimeConsumers.Sum(r => r.QueryCount);
        IReadOnlyList<RuntimeConsumerSummary> visibleRuntimeConsumers = canViewRuntimeConsumers
            ? runtimeConsumers
            : Array.Empty<RuntimeConsumerSummary>();

        string breakingChangeRisk = "LOW";
        if ((hasDashboards || hasPipelines) && (activeReadersCount >= 3 || totalAuditReads >= 50))
        {
            breakingChangeRisk = "CRITICAL";
        }
        else if (hasDashboards || hasPipelines)
        {
            breakingChangeRisk = "HIGH";
        }
        else if (hasExternalServices || activeReadersCount > 0)
        {
            breakingChangeRisk = "MEDIUM";
        }

        // 4. Actionable Mitigation Recommendations
        var mitigations = new List<string>();
        if (hasDashboards)
        {
            var dashNames = downstreamConsumers
                .Where(d => d.Type == LineageNodeType.Dashboard)
                .Select(d => d.Name)
                .Take(5);
            mitigations.Add($"Inform downstream dashboard teams before modifying or dropping columns: {string.Join(", ", dashNames)}.");
        }
        if (hasPipelines)
        {
            var pipeNames = downstreamConsumers
                .Where(d => d.Type == LineageNodeType.Pipeline)
                .Select(d => d.Name)
                .Take(5);
            mitigations.Add($"Coordinate maintenance windows with data ingestion / ETL pipelines: {string.Join(", ", pipeNames)}.");
        }
        if (activeReadersCount > 0)
        {
            mitigations.Add($"Table was accessed by {activeReadersCount} distinct active consumer(s) ({totalAuditReads} queries) in the last {timeWindowDays} days. Introduce a formal deprecation period of at least 14 days.");
            mitigations.Add("Provide schema backward-compatibility views or GraphQL field aliases during migration.");
        }
        else if (downstreamConsumers.Count == 0)
        {
            mitigations.Add("No active consumers or downstream dependencies detected. Schema modifications have minimal blast radius.");
        }

        sw.Stop();
        GatewayDiagnostics.LineageTraversalDuration.Record(sw.Elapsed.TotalMilliseconds);

        return new TableConsumersReport(
            rootTableId,
            breakingChangeRisk,
            downstreamConsumers.Count,
            activeReadersCount,
            lastAccessedAt,
            downstreamConsumers,
            visibleRuntimeConsumers,
            mitigations);
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

        var window = Math.Clamp(timeWindowDays, 1, 3650);
        var since = DateTimeOffset.UtcNow.AddDays(-window);
        var targetTableStr = table?.ToString();

        // 1. Query Audit Logs
        var auditLogs = _auditRepo != null
            ? await _auditRepo.QueryAuditLogsAsync(
                targetTable: targetTableStr,
                actorSid: subjectSid,
                since: since,
                limit: 5000,
                tenantId: callerContext?.TenantId,
                ct: ct).ConfigureAwait(false)
            : Array.Empty<AuditLogEntry>();

        var allowedLogs = auditLogs
            .Where(a => string.Equals(a.Decision, "ALLOW", StringComparison.OrdinalIgnoreCase))
            .ToList();

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
            "Art. 15 Abs. 1 Bst. c DSGVO: Auskunft über die Empfänger oder Kategorien von Empfängern, gegenüber denen die personenbezogenen Daten offengelegt worden sind oder noch offengelegt werden.");
    }

    public sealed class LineagePathNode(string nodeId, LineagePathNode? parent)
    {
        public string NodeId { get; } = nodeId;
        public LineagePathNode? Parent { get; } = parent;

        public bool Contains(string id)
        {
            var curr = this;
            while (curr != null)
            {
                if (string.Equals(curr.NodeId, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                curr = curr.Parent;
            }
            return false;
        }
    }
}
