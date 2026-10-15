namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

/// <summary>
/// Core security and governance visitor implementing:
/// - Row-Level Security (RLS) injection (subquery encapsulation and direct WHERE conjunction)
/// - Reserved alias binding (RowFilterAliases.Target) for correlated row filters
/// - Lexical CTE scope tracking (exit-timing and isolation)
/// - Column masking expansion and delimited quoting guarantees (SEC M-24)
/// - Consent-filtered table write protection (SQ-07) and row_scope_key preservation
/// - DML guardrails: anti-tautology filter checks (RejectUnfilteredDml) and WITH CHECK OPTION verification
/// </summary>
public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    private readonly AstSecurityVisitorOptions _options;
    private readonly Stack<HashSet<string>> _cteScopeStack = new();
    private readonly PolicyFilterCteGuard _cteFilterGuard = new();

    /// <summary>CTE names are compared by ordinal bytes: a delimited physical name that differs in case is not the CTE (INV-11).</summary>
    private static StringComparer CteScopeComparer =>
        CompilerHooks.IsDisabled(SecurityControl.CteCaseInsensitiveScope) ? StringComparer.OrdinalIgnoreCase :
        StringComparer.Ordinal;

    // CR-ADG-32: names of non-recursive CTEs whose body is being visited; a reference to one of them (not shadowed) is a self-reference.
    private readonly List<string> _definingNonRecursiveCtes = new();
    private readonly TypedPolicyContext _typed;
    private readonly Stack<SecurityScope> _securityScopes = new();
    private int _subqueryDepth = 0;
    private bool _rootLimitHandled = false;

    /// <param name="options">The switches the typed compile path derives from the request (never from the caller's text).</param>
    /// <param name="typedPolicy">
    /// Row-level security comes from typed providers: injected predicates are wrapped in <see cref="SecurityPredicateExpression"/>,
    /// physical names come from the catalog, and values are parameters. There is no string policy path.
    /// </param>
    internal AstSecurityVisitor(AstSecurityVisitorOptions options, TypedPolicyContext typedPolicy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(typedPolicy);
        _options = options;
        _typed = typedPolicy;
        _cteScopeStack.Push(new HashSet<string>(CteScopeComparer));
    }

    private SecurityScope CurrentScope =>
        _securityScopes.Count > 0 ? _securityScopes.Peek() : (_subqueryDepth <= 1 ? SecurityScope.Root : SecurityScope.Subquery);

    private SqlNode WithScope(SecurityScope scope, Func<SqlNode> visit)
    {
        _securityScopes.Push(scope);
        try
        {
            return visit();
        }
        finally
        {
            _securityScopes.Pop();
        }
    }

    protected static IEnumerable<SqlNode> VisitChildren(SqlNode node) => node.EnumerateChildren();
}
