namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Reference-identity registry of correlated <c>EXISTS</c> nodes. SEC-RF107-10 (a): <c>Register</c> is called only from
/// <c>AdvancedRlsPredicateBuilder.CorrelatedExpression</c> (architecture test), so no other writer can mark an arbitrary node as a site.
/// The table is process-global but holds its keys weakly (<see cref="ConditionalWeakTable{TKey, TValue}"/> compares by reference and never
/// keeps a node alive): an entry disappears with its node, so registration cannot leak memory, and the value (<see cref="CorrelatedFilterSite"/>)
/// holds no reference to the node. A node that was not registered (a user AST, a node re-created by a rewriter) is simply not found.
/// </summary>
internal static class CorrelatedFilterSiteRegistry
{
    private static readonly ConditionalWeakTable<ExistsExpression, CorrelatedFilterSite> Sites = new();

    public static void Register(ExistsExpression node, CorrelatedFilterSite site)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(site);
        Sites.AddOrUpdate(node, site);
    }

    public static bool TryGet(ExistsExpression node, [MaybeNullWhen(false)] out CorrelatedFilterSite site)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Sites.TryGetValue(node, out site);
    }
}
