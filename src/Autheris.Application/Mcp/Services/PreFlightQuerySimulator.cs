namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using HotChocolate.Language;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pre-flight query simulation engine analyzing AST complexity, token volume, and hard safety limits for AI agents (F-AI-04).
/// </summary>
public sealed class PreFlightQuerySimulator(ILogger<PreFlightQuerySimulator> logger) : IPreFlightQuerySimulator
{
    private readonly ILogger<PreFlightQuerySimulator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public const int MaxTokenBudget = 4000;
    public const long MaxDbScanBytes = 1_000_000_000L; // 1 GB

    public Task<PreFlightQuerySimulationResult> SimulateQueryAsync(
        string query,
        TableIdentifier? targetTable = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(new PreFlightQuerySimulationResult(
                IsAllowed: false,
                EstimatedRowCount: 0,
                EstimatedDbBytesScan: 0,
                EstimatedResponseTokens: 0,
                AppliedMaskingRules: [],
                ActiveRlsFilters: [],
                OptimizationRecommendations: [],
                BlockReason: "The query must not be empty."
            ));
        }

        DocumentNode doc;
        try
        {
            doc = Utf8GraphQLParser.Parse(query);
        }
        catch (SyntaxException ex)
        {
            return Task.FromResult(new PreFlightQuerySimulationResult(
                IsAllowed: false,
                EstimatedRowCount: 0,
                EstimatedDbBytesScan: 0,
                EstimatedResponseTokens: 0,
                AppliedMaskingRules: [],
                ActiveRlsFilters: [],
                OptimizationRecommendations: [],
                BlockReason: $"GraphQL-Syntaxfehler: {ex.Message}"
            ));
        }

        int requestedLimit = -1;
        int fieldCount = 0;
        bool hasFilter = false;

        foreach (var def in doc.Definitions)
        {
            if (def is OperationDefinitionNode op)
            {
                CountFieldsAndInspectArgs(op.SelectionSet, ref fieldCount, ref requestedLimit, ref hasFilter);
            }
        }

        if (fieldCount == 0)
        {
            fieldCount = 5; // default fallback
        }

        int estimatedRows = requestedLimit > 0 ? requestedLimit : 100;
        int estimatedTokens = estimatedRows * fieldCount * 4;
        long estimatedScanBytes = (long)estimatedRows * fieldCount * 128L;

        var recommendations = new List<string>();
        if (requestedLimit <= 0)
        {
            recommendations.Add("Add a 'first' or 'limit' argument (e.g. first: 50) to bound the number of returned tokens.");
        }
        if (!hasFilter)
        {
            recommendations.Add("Add filter conditions to exclude irrelevant rows early.");
        }

        // Hard Safety Limits
        if (estimatedTokens > MaxTokenBudget)
        {
            return Task.FromResult(new PreFlightQuerySimulationResult(
                IsAllowed: false,
                EstimatedRowCount: estimatedRows,
                EstimatedDbBytesScan: estimatedScanBytes,
                EstimatedResponseTokens: estimatedTokens,
                AppliedMaskingRules: new[] { "COLUMN_MASKING_ACTIVE" },
                ActiveRlsFilters: new[] { "TENANT_ISOLATION_RLS" },
                OptimizationRecommendations: recommendations,
                BlockReason: $"The query produces an estimated {estimatedTokens} tokens and exceeds the Hard-Safety-Limit of {MaxTokenBudget} tokens. Page with 'first: 50' or select fewer fields."
            ));
        }

        if (estimatedScanBytes > MaxDbScanBytes)
        {
            return Task.FromResult(new PreFlightQuerySimulationResult(
                IsAllowed: false,
                EstimatedRowCount: estimatedRows,
                EstimatedDbBytesScan: estimatedScanBytes,
                EstimatedResponseTokens: estimatedTokens,
                AppliedMaskingRules: new[] { "COLUMN_MASKING_ACTIVE" },
                ActiveRlsFilters: new[] { "TENANT_ISOLATION_RLS" },
                OptimizationRecommendations: recommendations,
                BlockReason: $"The estimated database scan ({estimatedScanBytes / (1024 * 1024)} MB) exceeds the limit of 1 GB. Filter the query."
            ));
        }

        return Task.FromResult(new PreFlightQuerySimulationResult(
            IsAllowed: true,
            EstimatedRowCount: estimatedRows,
            EstimatedDbBytesScan: estimatedScanBytes,
            EstimatedResponseTokens: estimatedTokens,
            AppliedMaskingRules: new[] { "COLUMN_MASKING_ACTIVE" },
            ActiveRlsFilters: new[] { "TENANT_ISOLATION_RLS" },
            OptimizationRecommendations: recommendations,
            BlockReason: null
        ));
    }

    private const int MaxAstDepth = 40;

    private static void CountFieldsAndInspectArgs(
        SelectionSetNode selectionSet,
        ref int fieldCount,
        ref int limit,
        ref bool hasFilter,
        int currentDepth = 0)
    {
        if (currentDepth > MaxAstDepth)
        {
            return;
        }

        foreach (var sel in selectionSet.Selections)
        {
            if (sel is FieldNode fn)
            {
                fieldCount++;

                foreach (var arg in fn.Arguments)
                {
                    var argName = arg.Name.Value;
                    if (argName is "first" or "limit" or "top" or "take")
                    {
                        if (arg.Value is IntValueNode ivn && int.TryParse(ivn.Value, out var parsed))
                        {
                            limit = parsed;
                        }
                    }
                    else if (argName is "filter" or "where")
                    {
                        hasFilter = true;
                    }
                }

                if (fn.SelectionSet != null)
                {
                    CountFieldsAndInspectArgs(fn.SelectionSet, ref fieldCount, ref limit, ref hasFilter, currentDepth + 1);
                }
            }
        }
    }
}
