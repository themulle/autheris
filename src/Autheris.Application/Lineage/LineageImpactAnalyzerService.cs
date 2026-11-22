namespace Autheris.Application.Lineage;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Usage;
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
    private readonly IUsageRepository? _usageRepo;
    private readonly ILogger<LineageImpactAnalyzerService> _logger;

    public LineageImpactAnalyzerService(
        IConsentRepository consentRepo,
        IDataOwnershipRepository ownershipRepo,
        ILineageGraphStore graphStore,
        IAuditLogRepository auditRepo,
        ITableMetadataRepository tableMetadataRepo,
        ILogger<LineageImpactAnalyzerService> logger,
        IUsageRepository? usageRepo = null)
    {
        _consentRepo = consentRepo ?? throw new ArgumentNullException(nameof(consentRepo));
        _ownershipRepo = ownershipRepo ?? throw new ArgumentNullException(nameof(ownershipRepo));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _auditRepo = auditRepo;
        _tableMetadataRepo = tableMetadataRepo;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _usageRepo = usageRepo;
    }

    public LineageImpactAnalyzerService(
        IConsentRepository consentRepo,
        IDataOwnershipRepository ownershipRepo,
        ILineageGraphStore graphStore,
        ILogger<LineageImpactAnalyzerService> logger)
        : this(consentRepo, ownershipRepo, graphStore, null!, null!, logger, null)
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
        // M-15: No exception for consents without a tenant (previously visible across tenants).
        if (consent == null ||
            (consent.TenantId != tenant &&
             !callerContext.IsClusterAdmin))
        {
            throw new KeyNotFoundException($"Consent with ID '{consentId}' not found.");
        }

        // M-15: Only the table's Owner/delegates, GovernanceAdmin, and PrivacyAdmin may view impact analysis.
        if (!await IsAuthorizedForAccessAnalysisAsync(consent.TableIdentifier, callerContext, ct).ConfigureAwait(false))
        {
            throw new GatewayForbiddenException("Impact analyses require Data Owner, GovernanceAdmin or PrivacyAdmin permissions.");
        }

        var rootTableId = consent.TableIdentifier.ToString();
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.Traverse", "tenant.id", tenant.Value);
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.Traverse", "root.node_id", rootTableId);

        // Zero-trust column authorization for ownerEmail:
        // Only GovernanceAdmin, ClusterAdmin, or the registered Data Owner of the affected table may see emails
        bool canViewEmail = callerContext.IsGovernanceAdmin || callerContext.IsClusterAdmin;
        if (!canViewEmail)
        {
            canViewEmail = await _ownershipRepo.IsAuthorizedApproverForTableAsync(consent.TableIdentifier, callerContext.UserSid, ct).ConfigureAwait(false);
        }

        int initialCap = _graphStore.GetAllNodes() is { Count: > 0 } all ? Math.Min(4096, all.Count) : 64;
        var queue = new Queue<(string CurrentId, LineagePathNode Path)>(capacity: initialCap);
        var visited = new HashSet<string>(initialCap, StringComparer.OrdinalIgnoreCase);
        var affectedMap = new Dictionary<string, AffectedEntity>(initialCap, StringComparer.OrdinalIgnoreCase);
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
                if (!visited.Add(downstreamId))
                {
                    if (path.Contains(downstreamId))
                    {
                        // Real cycle: downstreamId lies on the path from root to this node
                        containsCycles = true;
                        _logger.LogWarning("Lineage cycle detected at node: {NodeId} -> {DownstreamId}", currentId, downstreamId);

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
                    }
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

        // Severity calculation:
        // HIGH when dashboards or pipelines are affected
        // MEDIUM for ExternalService
        // LOW for tables/columns only
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

        // M-15: The access log (actor SIDs, access counts, time windows) is visible only to Owner/delegates,
        // GovernanceAdmin, and PrivacyAdmin. Other callers receive aggregated counts only.
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

        // 2b. Daily Pre-aggregated Application Metrics (Issue #114)
        IReadOnlyList<CallingApplicationUsage>? applicationUsages = null;
        bool suppressedSmallGroups = false;

        if (_usageRepo != null && callerContext is not null)
        {
            var window = Math.Clamp(timeWindowDays, 1, 400);
            var since = DateTimeOffset.UtcNow.AddDays(-window);
            var tenantId = callerContext.TenantId;

            var summaries = await _usageRepo.QueryConsumerAppDailyAsync(
                table, tenantId, since, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            var actorCounts = await _usageRepo.QueryDistinctActorCountsAsync(
                table, tenantId, since, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

            if (canViewRuntimeConsumers)
            {
                // Privileged Tier: Data Owner, Delegate, GovernanceAdmin
                // Expose personal breakdown (ActorSids), exact query names, no folding
                var groupedSummaries = ConsumerFolding.GroupByClient(summaries);
                var privilegedUsages = new List<CallingApplicationUsage>();
                foreach (var s in groupedSummaries)
                {
                    int actorCount = s.DistinctActors;
                    if (actorCounts.TryGetValue(s.Application, out var count))
                    {
                        actorCount = Math.Max(actorCount, count);
                    }

                    var matchingSids = runtimeConsumers.Select(r => r.ActorSid).Distinct().ToList();

                    privilegedUsages.Add(new CallingApplicationUsage(
                        Application: s.Application,
                        Day: s.Day,
                        Calls: s.Calls,
                        RowsReturned: s.RowsReturned,
                        RowsMeasuredCalls: s.RowsMeasuredCalls,
                        Rejections: s.Rejections,
                        Versions: s.Versions,
                        QueryNames: s.QueryNames,
                        DistinctActors: actorCount,
                        ActorSids: matchingSids,
                        ClientId: s.ClientId,
                        IsClientAsserted: s.IsClientAsserted ?? false
                    ));
                }
                applicationUsages = privilegedUsages;
                suppressedSmallGroups = false;
            }
            else
            {
                // General Tier: Non-privileged caller
                // Personal SIDs suppressed, query names suppressed, k-anonymity (k=3) cell folding applied
                var (folded, suppressed) = ConsumerFolding.Fold(summaries, minActors: 3);
                applicationUsages = folded;
                suppressedSmallGroups = suppressed;
            }
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
            mitigations,
            applicationUsages,
            suppressedSmallGroups);
    }

    /// <summary>
    /// Access and impact analyses are restricted to the table's Data Owner/delegates and GovernanceAdmin.
    /// </summary>
    private async Task<bool> IsAuthorizedForAccessAnalysisAsync(TableIdentifier table, CallerSecurityContext callerContext, CancellationToken ct)
    {
        if (callerContext.IsGovernanceAdmin ||
            callerContext.Roles.Any(r =>
                string.Equals(r, "GovernanceAdmin", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(callerContext.UserSid.Value))
        {
            return false;
        }

        return await _ownershipRepo.IsAuthorizedApproverForTableAsync(table, callerContext.UserSid, ct).ConfigureAwait(false);
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
