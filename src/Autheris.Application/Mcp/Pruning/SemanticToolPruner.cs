namespace Autheris.Application.Mcp.Pruning;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-AI-07: High-performance semantic tool pruner.
/// Ranks tools against agent prompt semantics and enforces strict token-budget limits.
/// </summary>
public sealed class SemanticToolPruner : ISemanticToolPruner
{
    private readonly ILogger<SemanticToolPruner> _logger;
    private static readonly Regex TokenSplitRegex = new(@"[\s\.,;:\-_/\\()\[\]{}""'|]+", RegexOptions.Compiled);

    public SemanticToolPruner(ILogger<SemanticToolPruner> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValueTask<IReadOnlyList<McpToolDefinition>> PruneToolsAsync(
        string userPrompt,
        IReadOnlyList<McpToolDefinition> availableTools,
        ToolPruningOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(availableTools);

        if (availableTools.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<McpToolDefinition>>(Array.Empty<McpToolDefinition>());
        }

        var opt = options ?? new ToolPruningOptions();

        // If tools count is already small and fits in budget, no pruning needed
        if (availableTools.Count <= opt.MaxTools)
        {
            var totalEstTokens = availableTools.Sum(EstimateToolTokens);
            if (totalEstTokens <= opt.MaxToolDefinitionTokens)
            {
                return ValueTask.FromResult(availableTools);
            }
        }

        // Sanitize prompt for scoring: extract keywords, ignoring control/injection characters
        var promptTerms = ExtractNormalizedTerms(userPrompt);

        var scoredTools = new List<(McpToolDefinition Tool, float Score)>(availableTools.Count);

        foreach (var tool in availableTools)
        {
            var score = CalculateRelevanceScore(tool, promptTerms, opt);
            scoredTools.Add((tool, score));
        }

        // Order by score descending
        var ranked = scoredTools
            .OrderByDescending(t => t.Score)
            .ToList();

        var selected = new List<McpToolDefinition>();
        var currentTokenCount = 0;

        foreach (var (tool, score) in ranked)
        {
            if (selected.Count >= opt.MaxTools)
            {
                break;
            }

            // Qualify tool if score >= threshold or if forced golden query
            var isGolden = opt.ForceIncludeGoldenQueries &&
                           (tool.Name.Contains("golden", StringComparison.OrdinalIgnoreCase) ||
                            tool.Description.Contains("golden query", StringComparison.OrdinalIgnoreCase));

            if (!isGolden && score < opt.MinSimilarityThreshold && selected.Count >= 3)
            {
                // Below threshold and we already have baseline tools
                continue;
            }

            var toolTokens = EstimateToolTokens(tool);
            if (currentTokenCount + toolTokens > opt.MaxToolDefinitionTokens && selected.Count > 0)
            {
                // Token budget exceeded
                continue;
            }

            selected.Add(tool);
            currentTokenCount += toolTokens;
        }

        // Safety fallback: if nothing selected, provide top 3
        if (selected.Count == 0)
        {
            selected.AddRange(availableTools.Take(Math.Min(3, opt.MaxTools)));
        }

        _logger.LogInformation(
            "F-AI-07 Pruned MCP tools from {Total} to {Selected} (est. {Tokens} tokens, budget: {Budget})",
            availableTools.Count, selected.Count, currentTokenCount, opt.MaxToolDefinitionTokens);

        return ValueTask.FromResult<IReadOnlyList<McpToolDefinition>>(selected);
    }

    private static float CalculateRelevanceScore(McpToolDefinition tool, HashSet<string> promptTerms, ToolPruningOptions options)
    {
        if (promptTerms.Count == 0)
        {
            return 0.5f; // Neutral default
        }

        var toolTerms = ExtractNormalizedTerms(tool.Name)
            .Concat(ExtractNormalizedTerms(tool.Description))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (tool.TargetTable.HasValue)
        {
            toolTerms.Add(tool.TargetTable.Value.TableName.ToLowerInvariant());
            toolTerms.Add(tool.TargetTable.Value.Schema.ToLowerInvariant());
            toolTerms.Add(tool.TargetTable.Value.Domain.ToLowerInvariant());
        }

        int matches = 0;
        int weightedScore = 0;

        foreach (var term in promptTerms)
        {
            if (toolTerms.Contains(term))
            {
                matches++;
                weightedScore += 3;
            }
            else
            {
                // Substring match check
                foreach (var tTerm in toolTerms)
                {
                    if (tTerm.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        term.Contains(tTerm, StringComparison.OrdinalIgnoreCase))
                    {
                        matches++;
                        weightedScore += 1;
                        break;
                    }
                }
            }
        }

        if (options.ForceIncludeGoldenQueries &&
            (tool.Name.Contains("golden", StringComparison.OrdinalIgnoreCase) ||
             tool.Description.Contains("golden query", StringComparison.OrdinalIgnoreCase)))
        {
            weightedScore += 5;
        }

        float maxPossible = promptTerms.Count * 3.0f;
        return maxPossible > 0 ? Math.Min(1.0f, weightedScore / maxPossible) : 0.5f;
    }

    private static HashSet<string> ExtractNormalizedTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return TokenSplitRegex.Split(text)
            .Where(t => t.Length >= 3 && !IsCommonStopword(t))
            .Select(t => t.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsCommonStopword(string word)
    {
        return word switch
        {
            "the" or "and" or "for" or "with" or "that" or "this" or "from" or "what" or "how" or
            "can" or "you" or "der" or "die" or "das" or "und" or "mit" or "für" or "von" or "auf" => true,
            _ => false
        };
    }

    private static int EstimateToolTokens(McpToolDefinition tool)
    {
        var chars = tool.Name.Length +
                    tool.Description.Length +
                    (tool.InputJsonSchema?.Length ?? 0) +
                    (tool.TargetGraphQLOperation?.Length ?? 0);
        return Math.Max(10, chars / 4);
    }
}
