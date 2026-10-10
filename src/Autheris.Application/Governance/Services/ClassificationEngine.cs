namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// High-performance classification engine supporting 100% deterministic regex pattern matching
/// and optional AI-assisted pre-classification.
/// </summary>
public sealed class ClassificationEngine : IClassificationEngine
{
    private readonly IOptionsMonitor<GatewayOptions> _gatewayOptions;
    private readonly IClassificationAiProvider? _aiProvider;
    private readonly ILogger<ClassificationEngine> _logger;
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.OrdinalIgnoreCase);

    public ClassificationEngine(
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        ILogger<ClassificationEngine> logger,
        IClassificationAiProvider? aiProvider = null)
    {
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _aiProvider = aiProvider;
    }

    public async ValueTask<TableClassificationProposal> ClassifyTableAsync(
        TableClassificationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = _gatewayOptions.CurrentValue.Classification ?? new ClassificationOptions();
        var profile = options.Workflow.GetActiveProfile();

        // 1. Resolve Owner if not present
        var resolvedOwner = ResolveOwner(context, profile.OwnerResolution);

        // 2. Perform deterministic classification
        var deterministicProposals = ClassifyColumnsDeterministic(context.Columns, options);

        // 3. Optional AI pre-classification if enabled
        IReadOnlyList<ColumnClassificationProposal> finalProposals = deterministicProposals;
        if (string.Equals(profile.PreClassification.Mode, "HumanInTheLoop", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profile.PreClassification.Mode, "AutoApproveUnambiguous", StringComparison.OrdinalIgnoreCase))
        {
            if (_aiProvider != null)
            {
                try
                {
                    var aiResult = await _aiProvider.ProposeClassificationsAsync(
                        context,
                        options.SensitivityLevels,
                        options.PiiCategories,
                        ct).ConfigureAwait(false);

                    if (aiResult != null && aiResult.Count == context.Columns.Count)
                    {
                        finalProposals = aiResult;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AI pre-classification failed for table {Table}; falling back to deterministic regex classifier.", context.TableIdentifier);
                }
            }
        }

        var initialStatus = string.IsNullOrWhiteSpace(resolvedOwner)
            ? "UNCLASSIFIED"
            : "PENDING_OWNER_REVIEW";

        return new TableClassificationProposal(
            Guid.NewGuid(),
            context.TableIdentifier,
            resolvedOwner,
            initialStatus,
            finalProposals,
            DateTimeOffset.UtcNow);
    }

    private string? ResolveOwner(TableClassificationContext context, OwnerResolutionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(context.ExistingDataOwnerSid))
        {
            return context.ExistingDataOwnerSid;
        }

        // Strategy A: Metadata tags (dbt meta.owner, data_owner, owner)
        if (context.MetadataTags != null)
        {
            var tagKeys = config.ExtractFromMetadataTags ?? new[] { "owner", "data_owner", "dbt_meta_owner" };
            foreach (var key in tagKeys)
            {
                if (context.MetadataTags.TryGetValue(key, out var ownerVal) && !string.IsNullOrWhiteSpace(ownerVal))
                {
                    return ownerVal.Trim();
                }
            }
        }

        // Strategy B: Rule-based regex mappings
        if (config.RuleBasedMappings != null)
        {
            var tableFullName = context.TableIdentifier.ToQualifiedName();
            foreach (var (pattern, owner) in config.RuleBasedMappings)
            {
                if (IsRegexMatch(tableFullName, pattern))
                {
                    return owner;
                }
            }
        }

        return null;
    }

    private IReadOnlyList<ColumnClassificationProposal> ClassifyColumnsDeterministic(
        IReadOnlyList<ColumnMetadataContext> columns,
        ClassificationOptions options)
    {
        var result = new List<ColumnClassificationProposal>(columns.Count);
        var defaultLevel = FindSensitivityLevel(options.DefaultSensitivityKey, options.SensitivityLevels);

        foreach (var col in columns)
        {
            var matchedCategories = new List<PiiCategoryDefinition>();

            foreach (var category in options.PiiCategories)
            {
                foreach (var pattern in category.NamePatterns)
                {
                    if (IsRegexMatch(col.ColumnName, pattern))
                    {
                        matchedCategories.Add(category);
                        break;
                    }
                }
            }

            if (matchedCategories.Count == 1)
            {
                var matched = matchedCategories[0];
                var level = FindSensitivityLevelByRank(matched.DefaultSensitivityRank, options.SensitivityLevels);

                result.Add(new ColumnClassificationProposal(
                    col.ColumnName,
                    col.DataType,
                    level.Key,
                    level.Rank,
                    matched.Key,
                    matched.DefaultMaskingRule,
                    Confidence: 0.95,
                    IsDisputed: false,
                    Reasoning: $"Deterministic regex match for PII category '{matched.Key}'"));
            }
            else if (matchedCategories.Count > 1)
            {
                // Disputed: Multiple competing PII categories matched
                var highest = matchedCategories[0];
                foreach (var cat in matchedCategories)
                {
                    if (cat.DefaultSensitivityRank > highest.DefaultSensitivityRank)
                    {
                        highest = cat;
                    }
                }

                var level = FindSensitivityLevelByRank(highest.DefaultSensitivityRank, options.SensitivityLevels);
                result.Add(new ColumnClassificationProposal(
                    col.ColumnName,
                    col.DataType,
                    level.Key,
                    level.Rank,
                    highest.Key,
                    highest.DefaultMaskingRule,
                    Confidence: 0.65,
                    IsDisputed: true,
                    Reasoning: $"Disputed: Multiple PII patterns matched ({string.Join(", ", matchedCategories.ConvertAll(c => c.Key))})"));
            }
            else
            {
                // No PII pattern matched -> Default sensitivity
                result.Add(new ColumnClassificationProposal(
                    col.ColumnName,
                    col.DataType,
                    defaultLevel.Key,
                    defaultLevel.Rank,
                    DetectedPiiCategoryKey: null,
                    ProposedMaskingRule: "NONE",
                    Confidence: 0.90,
                    IsDisputed: false,
                    Reasoning: "No PII pattern matched; assigned default sensitivity"));
            }
        }

        return result;
    }

    private bool IsRegexMatch(string input, string pattern)
    {
        var regex = _regexCache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50)));
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static SensitivityLevelDefinition FindSensitivityLevel(string key, IReadOnlyList<SensitivityLevelDefinition> levels)
    {
        foreach (var l in levels)
        {
            if (string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return l;
            }
        }
        return levels.Count > 0 ? levels[0] : new("L2_INTERNAL", "Intern", "Default", 2);
    }

    private static SensitivityLevelDefinition FindSensitivityLevelByRank(int rank, IReadOnlyList<SensitivityLevelDefinition> levels)
    {
        foreach (var l in levels)
        {
            if (l.Rank == rank)
            {
                return l;
            }
        }
        return FindSensitivityLevel("L2_INTERNAL", levels);
    }
}
