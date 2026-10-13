namespace TrinoSqlEngine.Ast.Builder;

using System;
using System.Collections.Generic;
using TrinoSqlEngine;

/// <summary>
/// Configuration options for the AST builder, including depth limits and policy enforcement switches.
/// </summary>
public sealed record AstBuilderOptions
{
    public int MaxAllowedAstDepth { get; init; } = 64;
    public bool EnforceReadOnlyQueries { get; init; } = true;
    public bool EnforceFunctionPolicy { get; init; } = true;
    public IReadOnlySet<string>? AllowedFunctions { get; init; }
    public IReadOnlySet<string>? AdditionalDeniedFunctions { get; init; }
    public IReadOnlySet<string>? AllowedTableFunctions { get; init; }
    public IReadOnlySet<string>? AllowedSessionProperties { get; init; }
    public bool AllowInlineFunctionDefinitions { get; init; } = false;
    public bool RejectTimeTravelQueries { get; init; } = true;

    /// <summary>Translate the Trino date functions <c>date_add</c>, <c>date_trunc</c>, <c>now()</c> and <c>x ± INTERVAL</c> into the date arithmetic of the target dialect.</summary>
    public bool TranslateTrinoDateFunctions { get; init; }
}
