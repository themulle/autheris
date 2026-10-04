namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class SchemaSunsettingService(ILogger<SchemaSunsettingService>? logger = null) : ISchemaSunsettingService
{
    private readonly ConcurrentDictionary<string, FieldSunsettingRule> _rulesByField = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _fieldKeysById = new();
    private readonly ILogger<SchemaSunsettingService> _logger = logger ?? NullLogger<SchemaSunsettingService>.Instance;

    private static string BuildKey(string targetTable, string fieldName) => $"{targetTable.Trim()}:{fieldName.Trim()}";

    public ValueTask RegisterRuleAsync(FieldSunsettingRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.TargetTable, nameof(rule.TargetTable));
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.FieldName, nameof(rule.FieldName));

        var key = BuildKey(rule.TargetTable, rule.FieldName);
        _rulesByField[key] = rule;
        _fieldKeysById[rule.Id] = key;

        _logger.LogInformation(
            "Registered sunsetting rule for '{Table}.{Field}'. Deprecated: {DeprecatedAt:yyyy-MM-dd}, Sunset: {SunsetAt:yyyy-MM-dd}",
            rule.TargetTable, rule.FieldName, rule.DeprecatedAt, rule.SunsetAt);

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<FieldSunsettingRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FieldSunsettingRule> list = [.. _rulesByField.Values];
        return ValueTask.FromResult(list);
    }

    public ValueTask<FieldSunsettingRule?> GetRuleAsync(string targetTable, string fieldName, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(targetTable, fieldName);
        _rulesByField.TryGetValue(key, out var rule);
        return ValueTask.FromResult(rule);
    }

    public ValueTask<bool> RemoveRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (_fieldKeysById.TryRemove(id, out var key))
        {
            return ValueTask.FromResult(_rulesByField.TryRemove(key, out _));
        }

        return ValueTask.FromResult(false);
    }

    public ValueTask<SunsettingEvaluationResult?> EvaluateFieldAsync(
        string targetTable,
        string fieldName,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(targetTable, fieldName);
        if (!_rulesByField.TryGetValue(key, out var rule) || !rule.IsActive)
        {
            return ValueTask.FromResult<SunsettingEvaluationResult?>(null);
        }

        var currentTime = now ?? DateTimeOffset.UtcNow;
        var phase = rule.DeterminePhase(currentTime);

        if (phase == SunsettingPhase.Active)
        {
            return ValueTask.FromResult<SunsettingEvaluationResult?>(null);
        }

        bool shouldInjectLatency = false;
        int syntheticLatencyMs = 0;
        bool shouldRejectWith426 = false;
        bool isHardSunsetBlocked = false;
        string notice;

        var replacementHint = !string.IsNullOrWhiteSpace(rule.ReplacementField)
            ? $" Use replacement field '{rule.ReplacementField}'."
            : string.Empty;

        switch (phase)
        {
            case SunsettingPhase.Warning:
                notice = $"Field '{rule.FieldName}' on '{rule.TargetTable}' is deprecated since {rule.DeprecatedAt:yyyy-MM-dd} and will be sunset on {rule.SunsetAt:yyyy-MM-dd}.{replacementHint}";
                break;

            case SunsettingPhase.Brownout:
                // Chaos testing probabilistic impact
                var roll = Random.Shared.NextDouble();
                if (roll < rule.BrownoutPercentage)
                {
                    if (rule.BrownoutErrorCode == 426)
                    {
                        shouldRejectWith426 = true;
                    }

                    if (rule.BrownoutLatencyMs > 0)
                    {
                        shouldInjectLatency = true;
                        syntheticLatencyMs = rule.BrownoutLatencyMs;
                    }
                }

                notice = $"Field '{rule.FieldName}' on '{rule.TargetTable}' is undergoing sunset brownout testing (sunsetting on {rule.SunsetAt:yyyy-MM-dd}).{replacementHint}";
                break;

            case SunsettingPhase.HardSunset:
                isHardSunsetBlocked = true;
                notice = $"Field '{rule.FieldName}' on table '{rule.TargetTable}' was permanently decommissioned on {rule.SunsetAt:yyyy-MM-dd}.{replacementHint}";
                break;

            default:
                return ValueTask.FromResult<SunsettingEvaluationResult?>(null);
        }

        var httpSunsetHeader = rule.SunsetAt.ToString("r"); // RFC 8594 IMF-fixdate format
        var extensions = new Dictionary<string, object?>
        {
            ["phase"] = phase.ToString(),
            ["deprecatedAt"] = rule.DeprecatedAt.ToString("o"),
            ["sunsetAt"] = rule.SunsetAt.ToString("o"),
            ["replacementField"] = rule.ReplacementField,
            ["deprecationReason"] = rule.DeprecationReason,
            ["notice"] = notice
        };

        var result = new SunsettingEvaluationResult(
            Rule: rule,
            Phase: phase,
            ShouldInjectSyntheticLatency: shouldInjectLatency,
            SyntheticLatencyMs: syntheticLatencyMs,
            ShouldRejectWith426: shouldRejectWith426,
            IsHardSunsetBlocked: isHardSunsetBlocked,
            DeprecationNotice: notice,
            SunsetDate: rule.SunsetAt,
            HttpSunsetHeader: httpSunsetHeader,
            ExtensionsData: extensions
        );

        return ValueTask.FromResult<SunsettingEvaluationResult?>(result);
    }
}
