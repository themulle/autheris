namespace Autheris.Application.Sql.Policy;

using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>
/// SQLA-06: detects a nested query (EXISTS, IN (SELECT ...), scalar or quantified subquery) inside a typed policy predicate. A policy
/// predicate never starts with a query, so any <see cref="SelectStatement"/> the walk reaches is a subquery. Fail closed: an exception while
/// walking counts as "contains a subquery".
/// </summary>
internal sealed class PolicyPredicateSubqueryDetector : SqlAstRewriter
{
    private bool _found;

    public static bool ContainsSubquery(Expression expression)
    {
        try
        {
            var detector = new PolicyPredicateSubqueryDetector();
            detector.Visit(expression);
            return detector._found;
        }
        catch (Exception)
        {
            return true;
        }
    }

    public override SqlNode VisitSelectStatement(SelectStatement node)
    {
        _found = true;
        return node;
    }
}
