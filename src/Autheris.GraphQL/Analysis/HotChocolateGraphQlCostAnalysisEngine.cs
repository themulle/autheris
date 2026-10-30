namespace Autheris.GraphQL.Analysis;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Domain.Options;
using Autheris.GraphQL.Interceptors;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #175: Exact execution cost engine adapter reusing HotChocolate runtime AST cost model.
/// Reuses QueryCostAnalyzerRule over DocumentNode to guarantee zero drift between pre-flight analysis and execution.
/// </summary>
public sealed class HotChocolateGraphQlCostAnalysisEngine : IGraphQlCostAnalysisEngine
{
    private readonly IRequestExecutorProvider _executorProvider;
    private readonly GatewayOptions _options;

    public HotChocolateGraphQlCostAnalysisEngine(
        IRequestExecutorProvider executorProvider,
        IOptions<GatewayOptions>? options = null)
    {
        _executorProvider = executorProvider ?? throw new ArgumentNullException(nameof(executorProvider));
        _options = options?.Value ?? new GatewayOptions();
    }

    public async ValueTask<GraphQlCostAnalysisResult> CalculateCostAsync(
        DocumentNode document,
        IReadOnlyDictionary<string, object?>? variableValues = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var maxRootFields = _options.GraphQL.MaxRootFieldsPerOperation > 0
            ? _options.GraphQL.MaxRootFieldsPerOperation
            : 10;

        // SEC-GOV175-02: Hard root field limit per operation to prevent alias amplification attacks
        int maxRootFieldCount = QueryCostAnalyzerRule.CountMaxRootFields(document, maxRootFields);
        if (maxRootFieldCount > maxRootFields)
        {
            var maxAllowedComplexity = _options.GraphQL.MaxAllowedComplexity > 0
                ? _options.GraphQL.MaxAllowedComplexity
                : 500;

            return new GraphQlCostAnalysisResult(
                TotalCost: maxAllowedComplexity + 1,
                RootFieldCosts: new Dictionary<string, int>(StringComparer.Ordinal),
                MaxRootFieldsCount: maxRootFieldCount);
        }

        // SEC-GOV175-03: Zero database reads / in-memory schema reuse without pipeline execution
        var executor = await _executorProvider.GetExecutorAsync(cancellationToken: ct).ConfigureAwait(false);
        var schema = executor.Schema;

        // I417-SC-A02: no silent fallback; the rule reads the validated configured multiplier from costOptions.
        var defaultListMultiplier = _options.QueryCost.DefaultListMultiplier;

        var rule = new QueryCostAnalyzerRule(
            maxAllowedCost: _options.GraphQL.MaxAllowedComplexity > 0 ? _options.GraphQL.MaxAllowedComplexity : 500,
            defaultListMultiplier: defaultListMultiplier,
            maxResponseRows: _options.GraphQL.MaxResponseRows > 0 ? _options.GraphQL.MaxResponseRows : 1000,
            onQueryTooComplex: null,
            maxRootFields: maxRootFields,
            payloadRowMultiplier: 1,
            maxExecutionDepth: QueryCostAnalyzerRule.ResolveMaxExecutionDepth(_options.GraphQL),
            costOptions: _options.QueryCost);

        var (totalCost, rootFieldCosts) = rule.ComputeCostDetails(document, schema, variableValues);

        return new GraphQlCostAnalysisResult(
            TotalCost: totalCost,
            RootFieldCosts: rootFieldCosts,
            MaxRootFieldsCount: maxRootFieldCount);
    }
}
