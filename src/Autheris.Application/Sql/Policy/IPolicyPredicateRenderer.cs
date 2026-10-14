namespace Autheris.Application.Sql.Policy;

using System.Collections.Generic;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>Dialect text of a typed predicate. <see cref="Parameters"/> is keyed by the marker (<c>@__gql_rls_0</c>); values are never in <see cref="Sql"/>.</summary>
public sealed record RenderedPolicyPredicate(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>Renders the typed IR to dialect text for the non-AST string consumers (§7.3): one source of truth with the AST path.</summary>
public interface IPolicyPredicateRenderer
{
    /// <param name="targetAlias">Alias under which the filtered table is exposed; replaces the reserved <see cref="RowFilterAliases.Target"/> qualifier.</param>
    /// <exception cref="System.ArgumentOutOfRangeException">The dialect has no generator (no silent ANSI fallback).</exception>
    RenderedPolicyPredicate Render(PolicyPredicate predicate, TargetSqlDialect dialect, string targetAlias);
}
