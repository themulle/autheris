namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Reference-identity registry of correlated <c>EXISTS</c> nodes (<c>ConditionalWeakTable</c>). SEC-RF107-10: <c>Register</c> is called
/// only from <c>AdvancedRlsPredicateBuilder.CorrelatedExpression</c> (architecture test). WP-B owns the implementation.
/// </summary>
internal static class CorrelatedFilterSiteRegistry
{
    public static void Register(ExistsExpression node, CorrelatedFilterSite site)
        => throw new NotImplementedException("WP-B: CorrelatedFilterSiteRegistry.Register");

    public static bool TryGet(ExistsExpression node, out CorrelatedFilterSite site)
        => throw new NotImplementedException("WP-B: CorrelatedFilterSiteRegistry.TryGet");
}
