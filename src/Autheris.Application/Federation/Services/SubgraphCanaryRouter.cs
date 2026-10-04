namespace Autheris.Application.Federation.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-OPS-03: Subgraph Dynamic Feature Flags & Canary Traffic Splitting Router.
/// </summary>
public sealed class SubgraphCanaryRouter : ISubgraphCanaryRouter
{
    private readonly ConcurrentDictionary<string, SubgraphCanaryRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SubgraphCanaryRouter> _logger;

    public SubgraphCanaryRouter(ILogger<SubgraphCanaryRouter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RegisterRule(SubgraphCanaryRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.RuleId);
        _rules[rule.RuleId] = rule;
        _logger.LogInformation("Registered Subgraph Canary Rule '{RuleId}' for subgraph '{Subgraph}' (Variant: {Variant}).",
            rule.RuleId, rule.SubgraphName, rule.VariantName);
    }

    public void RemoveRule(string ruleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        _rules.TryRemove(ruleId, out _);
    }

    public ValueTask<SubgraphRoutingDecision> ResolveTargetAsync(
        string subgraphName,
        Uri defaultUri,
        ClaimsPrincipal? principal,
        string? tenantId,
        string? variantHeader,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subgraphName);
        ArgumentNullException.ThrowIfNull(defaultUri);

        var matchingRules = _rules.Values
            .Where(r => r.IsEnabled && string.Equals(r.SubgraphName, subgraphName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingRules.Count == 0)
        {
            return ValueTask.FromResult(new SubgraphRoutingDecision("default", defaultUri, false, "No active canary rules"));
        }

        var identityKey = principal?.Identity?.Name ?? tenantId ?? Guid.NewGuid().ToString();

        foreach (var rule in matchingRules)
        {
            // 1. Header match (e.g. X-Feature-Variant)
            if (!string.IsNullOrWhiteSpace(rule.HeaderValueMatch))
            {
                if (string.Equals(variantHeader, rule.HeaderValueMatch, StringComparison.OrdinalIgnoreCase))
                {
                    return ValueTask.FromResult(new SubgraphRoutingDecision(
                        rule.VariantName,
                        new Uri(rule.TargetUrl),
                        true,
                        $"Matched variant header '{variantHeader}'"));
                }
                if (!string.IsNullOrWhiteSpace(variantHeader))
                {
                    continue;
                }
            }

            // 2. Role requirement
            if (!string.IsNullOrWhiteSpace(rule.RequiredRole))
            {
                if (principal == null || !principal.IsInRole(rule.RequiredRole))
                {
                    continue;
                }

                return ValueTask.FromResult(new SubgraphRoutingDecision(
                    rule.VariantName,
                    new Uri(rule.TargetUrl),
                    true,
                    $"Matched user role '{rule.RequiredRole}'"));
            }

            // 3. Tenant allowlist
            if (rule.AllowedTenants != null && rule.AllowedTenants.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(tenantId) || !rule.AllowedTenants.Contains(tenantId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                return ValueTask.FromResult(new SubgraphRoutingDecision(
                    rule.VariantName,
                    new Uri(rule.TargetUrl),
                    true,
                    $"Matched tenant '{tenantId}'"));
            }

            // 4. Percentage-based canary weight
            if (rule.WeightPercent > 0)
            {
                var hash = Math.Abs(identityKey.GetHashCode());
                var bucket = hash % 100;
                if (bucket < rule.WeightPercent)
                {
                    return ValueTask.FromResult(new SubgraphRoutingDecision(
                        rule.VariantName,
                        new Uri(rule.TargetUrl),
                        true,
                        $"Deterministic weight rollout ({bucket} < {rule.WeightPercent}%)"));
                }
            }
        }

        return ValueTask.FromResult(new SubgraphRoutingDecision("default", defaultUri, false, "Default routing target"));
    }
}
