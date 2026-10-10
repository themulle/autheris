namespace TrinoSqlEngine.Ast.Nodes;

/// <summary>Identity of an injected row-level-security predicate: the secured table and an ordinal per table.</summary>
public readonly record struct SecurityPredicateId(string TableIdentity, int Ordinal);

/// <summary>Where in the statement a security predicate was injected (plan 4.3 and 16.4).</summary>
public enum SecurityScope
{
    Root,
    Subquery,
    CteBody,
    SetOperationBranch,
    Lateral,
    ScalarSubquery,
    ExistsSubquery,
    InSubquery,
    DmlTarget,
    DmlSource,
    MergeSource,
    MergeTarget,

    /// <summary>Tenant predicate of a physical table inside a policy subquery (SEC-ADG-11).</summary>
    PolicySubquery,

    /// <summary>MERGE target predicate in the ON condition.</summary>
    MergeOn,

    /// <summary>INSERT check option (CR-ADG-35): the row policy evaluated over the inserted values, in the WHERE of the insert source.</summary>
    InsertCheck
}

/// <summary>
/// An injected row-level-security predicate. It is opaque to every rewriter that runs after the security visitor (INV-3):
/// <see cref="Visitors.SqlAstRewriter"/> returns it unchanged without visiting <see cref="Predicate"/>, so no later pass can
/// weaken, fold or remove it. <see cref="Security.SecurityCoverageVerifier"/> proves its presence in the final tree.
/// </summary>
public sealed record SecurityPredicateExpression(
    Expression Predicate,
    SecurityPredicateId Id,
    SecurityScope Scope) : Expression;
