namespace TrinoSqlEngine.Ast.Builder;

using System;
using System.Collections.Generic;
using TrinoSqlEngine;

/// <summary>
/// Configuration options for the AST builder, including depth limits and policy enforcement switches.
/// </summary>
public sealed record AstBuilderOptions
{
    /// <summary>
    /// When true, a rejection raised while parsing client statement text may name the rejected function or construct.
    /// Only parsers of client text set it; every policy-text parser keeps the default so policy facts are never named.
    /// </summary>
    internal bool ExposeRejectionDetails { get; private init; }

    /// <summary>A copy that lets a rejection of client statement text name the rejected function or construct. Never call it for policy text.</summary>
    public AstBuilderOptions WithRejectionDetailsExposed() => this with { ExposeRejectionDetails = true };

    /// <summary>Maximum permitted AST nesting depth before parsing fails.</summary>
    public int MaxAllowedAstDepth { get; init; } = 64;

    /// <summary>When true, rejects non-SELECT statements during AST building.</summary>
    public bool EnforceReadOnlyQueries { get; init; } = true;

    /// <summary>When true, restricts SQL function calls to the configured allowlist.</summary>
    public bool EnforceFunctionPolicy { get; init; } = true;

    /// <summary>
    /// Explicitly permitted SQL function names; when null and <see cref="EnforceFunctionPolicy"/> is true, all functions are denied (fail closed).
    /// </summary>
    public IReadOnlySet<string>? AllowedFunctions { get; init; }

    /// <summary>Additional SQL function names denied even if otherwise present in the allowlist.</summary>
    public IReadOnlySet<string>? AdditionalDeniedFunctions { get; init; }

    /// <summary>Explicitly permitted table-valued SQL function names.</summary>
    public IReadOnlySet<string>? AllowedTableFunctions { get; init; }

    /// <summary>Explicitly permitted Trino session property names.</summary>
    public IReadOnlySet<string>? AllowedSessionProperties { get; init; }

    /// <summary>When true, permits inline SQL function definitions in statements.</summary>
    public bool AllowInlineFunctionDefinitions { get; init; } = false;

    /// <summary>When true, rejects SQL time-travel (<c>FOR SYSTEM_TIME</c> / <c>AS OF</c>) clauses.</summary>
    public bool RejectTimeTravelQueries { get; init; } = true;

    /// <summary>Translate the Trino date functions <c>date_add</c>, <c>date_trunc</c>, <c>now()</c> and <c>x ± INTERVAL</c> into the date arithmetic of the target dialect.</summary>
    public bool TranslateTrinoDateFunctions { get; init; }
}
