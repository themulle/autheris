namespace Autheris.Application.Governance.Analysis;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HotChocolate.Language;

/// <summary>
/// Issue #175: In-process GraphQL execution cost analysis engine abstraction.
/// Enables QueryAnalysisService in Autheris.Application to delegate to the runtime HotChocolate AST cost model
/// without violating clean architecture layer boundaries.
/// </summary>
public interface IGraphQlCostAnalysisEngine
{
    ValueTask<GraphQlCostAnalysisResult> CalculateCostAsync(
        DocumentNode document,
        IReadOnlyDictionary<string, object?>? variableValues = null,
        CancellationToken ct = default);
}

public sealed record GraphQlCostAnalysisResult(
    int TotalCost,
    IReadOnlyDictionary<string, int> RootFieldCosts,
    int MaxRootFieldsCount);
