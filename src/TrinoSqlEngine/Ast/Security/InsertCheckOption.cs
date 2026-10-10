namespace TrinoSqlEngine.Ast.Security;

using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// CR-ADG-35: the number of rows an INSERT with check-option semantics must affect. The coverage verifier has already proved the
/// shape of the statement; this only counts its row selects.
/// </summary>
internal static class InsertCheckOption
{
    /// <returns>The expected affected row count, or null when the statement is not a check-option INSERT.</returns>
    public static int? ExpectedRows(SqlStatement statement)
    {
        if (statement is not InsertStatement { Source: QuerySpecification { From: SubqueryTableSource derived, Where: { } where } } ||
            !string.Equals(derived.Alias.Value, Visitors.AstSecurityVisitor.InsertCheckAlias.Value, System.StringComparison.Ordinal) ||
            !HasCheckPredicate(where))
        {
            return null;
        }

        return Count(derived.Subquery.Body);
    }

    private static bool HasCheckPredicate(Expression where) => where switch
    {
        SecurityPredicateExpression { Scope: SecurityScope.InsertCheck } => true,
        ParenthesizedExpression p => HasCheckPredicate(p.Expression),
        BinaryExpression { Operator: BinaryOperator.And } b => HasCheckPredicate(b.Left) || HasCheckPredicate(b.Right),
        _ => false
    };

    private static int Count(QueryBody body) => body switch
    {
        SetOperationQuery setOp => Count(setOp.Left) + Count(setOp.Right),
        _ => 1
    };
}
