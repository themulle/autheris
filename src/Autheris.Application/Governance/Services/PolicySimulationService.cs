namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Casbin;
using Casbin.Model;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed partial class PolicySimulationService : IPolicySimulationService
{
    [GeneratedRegex(@"'([^']{2,})'")]
    private static partial Regex SubRuleQuoteRegex();

    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<PolicySimulationService> _logger;
    private readonly string _modelText;
    private readonly bool _modelSupportsWildcardTenant;

    public PolicySimulationService(
        IAuditLogRepository auditLogRepository,
        IPolicyEnforcementService? policyEnforcementService = null,
        IOptions<GatewayOptions>? options = null,
        ILogger<PolicySimulationService>? logger = null)
    {
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _logger = logger ?? NullLogger<PolicySimulationService>.Instance;

        if (policyEnforcementService is CasbinEnforcementService casbin)
        {
            _modelText = casbin.ModelText;
            _modelSupportsWildcardTenant = casbin.ModelSupportsWildcardTenant;
        }
        else if (options?.Value?.Casbin?.ModelPath is { Length: > 0 } modelPath && File.Exists(modelPath))
        {
            _modelText = File.ReadAllText(modelPath);
            var contract = CasbinModelContract.Verify(_modelText);
            _modelSupportsWildcardTenant = contract.SupportsWildcardTenant;
        }
        else
        {
            _modelText = CasbinEnforcementService.DefaultModelText;
            var contract = CasbinModelContract.Verify(_modelText);
            _modelSupportsWildcardTenant = contract.SupportsWildcardTenant;
        }
    }

    public async Task<PolicySimulationResult> SimulateAsync(
        PolicySimulationRequest request,
        TenantId effectiveTenant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DraftPolicyCsv, nameof(request.DraftPolicyCsv));

        // SEC H-05: The tenant scope is enforced by the caller (derived from the authenticated principal).
        // request.Tenant is untrusted client input and is never used to widen the audit-log query;
        // a null tenant (= all tenants) can no longer reach the repository.
        if (string.IsNullOrWhiteSpace(effectiveTenant.Value))
        {
            throw new ArgumentException("Security validation error: an effective tenant must be supplied for policy simulation.", nameof(effectiveTenant));
        }

        var limit = request.Limit <= 0 ? 500 : Math.Min(request.Limit, 10000);
        var tenantStr = effectiveTenant.Value;

        var enforcer = CreateSimulationEnforcer(request.DraftPolicyCsv, tenantStr);

        var auditEntries = await _auditLogRepository.QueryAuditLogsAsync(
            targetTable: request.TargetTable,
            actorSid: null,
            since: request.Since,
            limit: limit,
            tenantId: effectiveTenant,
            ct: cancellationToken
        ).ConfigureAwait(false);

        var differences = new List<PolicySimulationDifference>();
        var tableStats = new Dictionary<string, TableStatAccumulator>(StringComparer.OrdinalIgnoreCase);

        int totalEvaluated = 0;
        int allowedBaseline = 0;
        int deniedBaseline = 0;
        int allowedSimulation = 0;
        int deniedSimulation = 0;
        int newlyDenied = 0;
        int newlyAllowed = 0;

        foreach (var entry in auditEntries)
        {
            totalEvaluated++;
            var historicalDecision = entry.Decision?.Trim().ToUpperInvariant() ?? "ALLOW";
            if (historicalDecision != "DENY")
            {
                historicalDecision = "ALLOW";
                allowedBaseline++;
            }
            else
            {
                deniedBaseline++;
            }

            var actor = entry.ActorSid.Value;
            var table = string.IsNullOrWhiteSpace(entry.TargetTable) ? "*" : entry.TargetTable;
            var action = entry.EventType?.Contains("write", StringComparison.OrdinalIgnoreCase) == true ? "write" : "read";

            var targetTableId = TableIdentifier.TryParse(table, out var tid) ? tid : new TableIdentifier("default", "public", table);
            var evalContext = new SecurityEvaluationContext(
                UserSid: entry.ActorSid,
                GroupSids: Array.Empty<Sid>(),
                Tenant: effectiveTenant,
                TargetTable: targetTableId,
                RequestedColumns: Array.Empty<string>(),
                ClientIp: IPAddress.Loopback,
                Timestamp: entry.OccurredAt,
                PurposeId: null,
                Attributes: new Dictionary<string, object?>
                {
                    ["tenant"] = tenantStr,
                    ["user_sid"] = actor
                }
            );

            bool isAllowed = false;
            try
            {
                isAllowed = enforcer.Enforce(actor, tenantStr, table, action, evalContext);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Casbin evaluation error during simulation for subject '{Actor}' on table '{Table}'", actor, table);
                isAllowed = false;
            }

            var simulatedDecision = isAllowed ? "ALLOW" : "DENY";
            if (isAllowed)
            {
                allowedSimulation++;
            }
            else
            {
                deniedSimulation++;
            }

            if (!tableStats.TryGetValue(table, out var stat))
            {
                stat = new TableStatAccumulator(table);
                tableStats[table] = stat;
            }
            stat.Evaluated++;
            if (isAllowed) stat.Allowed++; else stat.Denied++;

            if (!string.Equals(historicalDecision, simulatedDecision, StringComparison.OrdinalIgnoreCase))
            {
                stat.Changed++;
                if (historicalDecision == "ALLOW" && simulatedDecision == "DENY")
                {
                    newlyDenied++;
                }
                else if (historicalDecision == "DENY" && simulatedDecision == "ALLOW")
                {
                    newlyAllowed++;
                }

                differences.Add(new PolicySimulationDifference(
                    AuditLogId: entry.Id,
                    OccurredAt: entry.OccurredAt,
                    ActorSid: entry.ActorSid,
                    TargetTable: table,
                    TargetColumn: entry.TargetColumn,
                    HistoricalDecision: historicalDecision,
                    SimulatedDecision: simulatedDecision,
                    Explanation: $"Subject '{actor}' was historically '{historicalDecision}' on '{table}', but simulated draft policy evaluated to '{simulatedDecision}'."
                ));
            }
        }

        var perTableSummaries = new Dictionary<string, TableSimulationSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in tableStats)
        {
            perTableSummaries[kvp.Key] = new TableSimulationSummary(
                TableName: kvp.Key,
                EvaluatedCount: kvp.Value.Evaluated,
                AllowedCount: kvp.Value.Allowed,
                DeniedCount: kvp.Value.Denied,
                ChangedCount: kvp.Value.Changed
            );
        }

        double impactPercentage = totalEvaluated > 0
            ? Math.Round(((double)(newlyDenied + newlyAllowed) / totalEvaluated) * 100.0, 2)
            : 0.0;

        return new PolicySimulationResult(
            TotalEvaluatedLogs: totalEvaluated,
            AllowedInBaseline: allowedBaseline,
            DeniedInBaseline: deniedBaseline,
            AllowedInSimulation: allowedSimulation,
            DeniedInSimulation: deniedSimulation,
            NewlyDeniedCount: newlyDenied,
            NewlyAllowedCount: newlyAllowed,
            ImpactPercentage: impactPercentage,
            PerTableSummaries: perTableSummaries,
            Differences: differences,
            SimulatedAt: DateTimeOffset.UtcNow
        );
    }

    private Enforcer CreateSimulationEnforcer(string policyCsv, string defaultTenant)
    {
        var (parsedRules, parsedGrouping) = CasbinEnforcementService.ParsePolicyText(
            policyCsv,
            defaultTenant,
            _modelSupportsWildcardTenant,
            allowWildcardForDefaultTenant: true);

        var model = DefaultModel.CreateFromText(_modelText);
        var enforcer = new Enforcer(model);

        foreach (var tenantRules in parsedRules.Values)
        {
            foreach (var rule in tenantRules)
            {
                var normalizedSubRule = SubRuleQuoteRegex().Replace(rule.SubRule, "\"$1\"");
                enforcer.AddPolicy(rule.Sub, rule.Tenant, rule.Obj, rule.Act, normalizedSubRule, rule.Eft);
            }
        }

        foreach (var g in parsedGrouping)
        {
            enforcer.AddGroupingPolicy(g.User, g.Role);
        }

        return enforcer;
    }

    private sealed class TableStatAccumulator(string tableName)
    {
        public string TableName { get; } = tableName;
        public int Evaluated { get; set; }
        public int Allowed { get; set; }
        public int Denied { get; set; }
        public int Changed { get; set; }
    }
}
