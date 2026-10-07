namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Casbin;
using Casbin.Model;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed partial class PolicySimulationService(
    IAuditLogRepository auditLogRepository,
    ILogger<PolicySimulationService>? logger = null) : IPolicySimulationService
{
    private static readonly string CasbinModelDefinition = CasbinEnforcementService.DefaultModelText;

    private static readonly string[] DangerousSubRuleTokens =
    [
        "System.", "System;", "Process", "File.", "Directory.", "Assembly", "GetType", "Activator",
        "Environment.", "AppDomain", "MethodInfo", "Invoke", "Type.", "TypeName", "Reflection",
        "DllImport", "Marshal", "Socket", "WebClient", "HttpClient", "Net.", "Unsafe", "Pointer",
        "Diagnostics.", "Compiler", "IO.", "Security.", "Microsoft.", "Configuration", "Registry"
    ];

    [GeneratedRegex(@"'([^']{2,})'")]
    private static partial Regex SubRuleQuoteRegex();

    private readonly IAuditLogRepository _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
    private readonly ILogger<PolicySimulationService> _logger = logger ?? NullLogger<PolicySimulationService>.Instance;

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

            bool isAllowed = false;
            try
            {
                isAllowed = enforcer.Enforce(actor, tenantStr, table, action, new { Tenant = tenantStr, UserSid = actor });
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

    private static Enforcer CreateSimulationEnforcer(string policyCsv, string defaultTenant)
    {
        var model = DefaultModel.CreateFromText(CasbinModelDefinition);
        var enforcer = new Enforcer(model);

        var lines = policyCsv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;

            var type = parts[0].ToLowerInvariant();
            if (type == "p")
            {
                // p, sub, tenant, obj, act, [subRule], [eft]
                if (parts.Length >= 4)
                {
                    var sub = parts[1];
                    var tenant = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : defaultTenant;
                    var obj = parts.Length > 3 ? parts[3] : "*";
                    var act = parts.Length > 4 ? parts[4] : "read";
                    var subRule = parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5]) ? parts[5] : "true";
                    var eft = parts.Length > 6 && !string.IsNullOrWhiteSpace(parts[6]) ? parts[6] : "allow";

                    ValidateSubRule(subRule);
                    var normalizedSubRule = SubRuleQuoteRegex().Replace(subRule, "\"$1\"");
                    enforcer.AddPolicy(sub, tenant, obj, act, normalizedSubRule, eft);
                }
            }
            else if (type == "g")
            {
                // g, user, role
                if (parts.Length >= 3)
                {
                    enforcer.AddGroupingPolicy(parts[1], parts[2]);
                }
            }
        }

        return enforcer;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_.\s()=<>!,'""+\-*/%:]+$", RegexOptions.Compiled)]
    private static partial Regex SafeSubRulePattern();

    private static void ValidateSubRule(string subRule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subRule);

        if (subRule.Length > 500)
        {
            throw new ArgumentException("Security validation error: Casbin sub_rule exceeds maximum length of 500 characters.", nameof(subRule));
        }

        if (!SafeSubRulePattern().IsMatch(subRule))
        {
            throw new ArgumentException("Security validation error: Casbin sub_rule contains disallowed characters.", nameof(subRule));
        }

        foreach (var token in DangerousSubRuleTokens)
        {
            if (subRule.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Security validation error: Casbin sub_rule in draft policy contains forbidden token '{token}'.", nameof(subRule));
            }
        }
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
