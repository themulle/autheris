using Autheris.Tests.Parity.Matrix;

namespace Autheris.Tests.Parity.Differential;

/// <summary>
/// One differential case (D-16): the question as plain SQLite SQL for the reference and as a channel request for the product.
/// <paramref name="ProductColumns"/> names the product columns when they differ from the reference aliases (GraphQL group keys).
/// </summary>
public sealed record DifferentialCase(
    string Id,
    string Capability,
    string Channel,
    string ReferenceSql,
    object? Parameters,
    MatrixRequest Request,
    bool Ordered,
    string[]? ProductColumns = null,
    string? GapId = null,
    bool RejectedByTypedAggregation = false)
{
    /// <summary>Case part of the parity cell id (the capability part already names the matrix capability).</summary>
    public string CellCaseId => Id.StartsWith("pairwise.", StringComparison.Ordinal) ? Id["pairwise.".Length..] : "ref";
}
