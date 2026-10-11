namespace Autheris.Tests.SqlCompilerGate.Classification;

using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;

/// <summary>
/// Directional classes of the legacy-vs-AST differential (plan 9.3 G4, 16.5 M-1). Every <c>ast-looser-*</c> class is a gate failure
/// and can never be accepted. <see cref="AstStricter"/> and <see cref="AstError"/> are accepted only through
/// <c>accepted-differences.json</c>.
/// </summary>
public enum DifferenceClass
{
    Identical,
    EquivalentResult,

    /// <summary>Both engines refused the statement. Not a difference.</summary>
    BothRejected,

    /// <summary>The AST path refused or returned strictly less (a governed refusal, or a strict subset of the legacy rows or writes).</summary>
    AstStricter,

    /// <summary>The AST path failed where legacy succeeded, with a database error or an unexpected exception type.</summary>
    AstError,

    /// <summary>Legacy refused and the AST path accepted, and the oracle found nothing wrong. Informational.</summary>
    AstAccepts,

    /// <summary>A row that legacy did not return (or the oracle flags a foreign or consent-excluded marker).</summary>
    AstLooserRows,

    /// <summary>A clear value of a masked column where legacy returned a masked value (or fewer clear values).</summary>
    AstLooserMask,

    /// <summary>DML changed a row that legacy did not change (or a row that is invisible to the principal).</summary>
    AstLooserWrite,

    /// <summary>Legacy refused, the AST path accepted, and the independent oracle flags the result or the write.</summary>
    AstLooserAccepts
}

public static class DifferenceClassExtensions
{
    public static bool IsLooser(this DifferenceClass c) =>
        c is DifferenceClass.AstLooserRows or DifferenceClass.AstLooserMask or DifferenceClass.AstLooserWrite or DifferenceClass.AstLooserAccepts;

    /// <summary>The wire name used in <c>accepted-differences.json</c> and in reports.</summary>
    public static string ToWireName(this DifferenceClass c) => c switch
    {
        DifferenceClass.Identical => "identical",
        DifferenceClass.EquivalentResult => "equivalent-result",
        DifferenceClass.BothRejected => "both-rejected",
        DifferenceClass.AstStricter => "ast-stricter",
        DifferenceClass.AstError => "ast-error",
        DifferenceClass.AstAccepts => "ast-accepts",
        DifferenceClass.AstLooserRows => "ast-looser-rows",
        DifferenceClass.AstLooserMask => "ast-looser-mask",
        DifferenceClass.AstLooserWrite => "ast-looser-write",
        DifferenceClass.AstLooserAccepts => "ast-looser-accepts",
        _ => throw new ArgumentOutOfRangeException(nameof(c))
    };
}

/// <summary>The classification of one statement: the class, a reason (exception type names only) and the oracle findings on the AST path.</summary>
public sealed record Classification(DifferenceClass Class, string Reason, IReadOnlyList<OracleViolation> AstViolations);

/// <summary>Classifies the pair of outcomes of one statement. Directional: a difference is judged by which side returned or wrote more.</summary>
public static class Classifier
{
    public static Classification Classify(EngineOutcome legacy, EngineOutcome ast, PolicyOracle oracle)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(oracle);

        var astViolations = ast switch
        {
            EngineOutcome.Rows r => oracle.CheckRows(r.Data),
            EngineOutcome.Written w => oracle.CheckWrite(w.Changes),
            _ => []
        };

        switch (legacy, ast)
        {
            case (EngineOutcome.Rejected, EngineOutcome.Rejected):
                return new(DifferenceClass.BothRejected, string.Empty, astViolations);

            case (_, EngineOutcome.Rejected a):
                return a.Stage == RejectionStage.Compile && a.Governed
                    ? new(DifferenceClass.AstStricter, a.Reason, astViolations)
                    : new(DifferenceClass.AstError, a.Reason, astViolations);

            case (EngineOutcome.Rejected, _):
                return astViolations.Count > 0
                    ? new(DifferenceClass.AstLooserAccepts, KindsOf(astViolations), astViolations)
                    : new(DifferenceClass.AstAccepts, string.Empty, astViolations);

            case (EngineOutcome.Rows l, EngineOutcome.Rows a):
                return ClassifyRows(l, a, oracle, astViolations);

            case (EngineOutcome.Written l, EngineOutcome.Written a):
                return ClassifyWrites(l, a, astViolations);

            default:
                // a query on one side and a write on the other cannot happen for one statement; fail closed
                return new(DifferenceClass.AstError, "OutcomeShapeMismatch", astViolations);
        }
    }

    private static Classification ClassifyRows(EngineOutcome.Rows legacy, EngineOutcome.Rows ast, PolicyOracle oracle, IReadOnlyList<OracleViolation> astViolations)
    {
        var rawL = CellNormalizer.Multiset(legacy.Data.Select(r => CellNormalizer.RowKey(r, CellNormalizer.Raw)));
        var rawA = CellNormalizer.Multiset(ast.Data.Select(r => CellNormalizer.RowKey(r, CellNormalizer.Raw)));
        if (SameMultiset(rawL, rawA)) return new(DifferenceClass.Identical, string.Empty, astViolations);

        var normL = CellNormalizer.Multiset(legacy.Data.Select(r => CellNormalizer.RowKey(r, CellNormalizer.Normalize)));
        var normA = CellNormalizer.Multiset(ast.Data.Select(r => CellNormalizer.RowKey(r, CellNormalizer.Normalize)));
        if (SameMultiset(normL, normA)) return new(DifferenceClass.EquivalentResult, string.Empty, astViolations);

        // direction: a clear masked value that legacy did not return
        if (oracle.CountClearMasked(ast.Data) > oracle.CountClearMasked(legacy.Data))
        {
            return new(DifferenceClass.AstLooserMask, "ClearMaskedValue", astViolations);
        }

        // direction: a row (or a foreign marker) that legacy did not return
        var legacyViolations = oracle.CheckRows(legacy.Data);
        bool newViolation = astViolations.Count(v => v.Kind != OracleViolationKind.ClearMaskedValue)
                            > legacyViolations.Count(v => v.Kind != OracleViolationKind.ClearMaskedValue);
        if (newViolation || HasSurplus(normA, normL))
        {
            return new(DifferenceClass.AstLooserRows, newViolation ? KindsOf(astViolations) : "SurplusRows", astViolations);
        }

        return new(DifferenceClass.AstStricter, "FewerRows", astViolations);
    }

    private static Classification ClassifyWrites(EngineOutcome.Written legacy, EngineOutcome.Written ast, IReadOnlyList<OracleViolation> astViolations)
    {
        var rawL = CellNormalizer.Multiset(legacy.Changes.Select(c => c.Sign + c.Table + CellNormalizer.RowKey(c.Cells, CellNormalizer.Raw)));
        var rawA = CellNormalizer.Multiset(ast.Changes.Select(c => c.Sign + c.Table + CellNormalizer.RowKey(c.Cells, CellNormalizer.Raw)));
        if (SameMultiset(rawL, rawA)) return new(DifferenceClass.Identical, string.Empty, astViolations);

        var normL = CellNormalizer.Multiset(legacy.Changes.Select(c => c.Sign + c.Table + CellNormalizer.RowKey(c.Cells, CellNormalizer.Normalize)));
        var normA = CellNormalizer.Multiset(ast.Changes.Select(c => c.Sign + c.Table + CellNormalizer.RowKey(c.Cells, CellNormalizer.Normalize)));
        if (SameMultiset(normL, normA)) return new(DifferenceClass.EquivalentResult, string.Empty, astViolations);

        if (astViolations.Count > 0 || HasSurplus(normA, normL))
        {
            return new(DifferenceClass.AstLooserWrite, astViolations.Count > 0 ? KindsOf(astViolations) : "SurplusWrites", astViolations);
        }

        return new(DifferenceClass.AstStricter, "FewerWrites", astViolations);
    }

    private static string KindsOf(IReadOnlyList<OracleViolation> violations) =>
        string.Join('+', violations.Select(v => v.Kind.ToString()).Distinct().Order(StringComparer.Ordinal));

    private static bool SameMultiset(Dictionary<string, int> a, Dictionary<string, int> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out int n) && n == p.Value);

    /// <summary>True when <paramref name="a"/> holds an element more often than <paramref name="b"/> (a is not a sub-multiset of b).</summary>
    private static bool HasSurplus(Dictionary<string, int> a, Dictionary<string, int> b) =>
        a.Any(p => p.Value > b.GetValueOrDefault(p.Key));
}
