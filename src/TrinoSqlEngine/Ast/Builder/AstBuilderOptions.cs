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

    public static AstBuilderOptions FromRlsOptions(RlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new AstBuilderOptions
        {
            EnforceReadOnlyQueries = options.EnforceReadOnlyQueries,
            EnforceFunctionPolicy = options.EnforceFunctionPolicy,
            AllowedFunctions = options.AllowedFunctions,
            AdditionalDeniedFunctions = options.AdditionalDeniedFunctions,
            AllowedTableFunctions = options.AllowedTableFunctions,
            AllowedSessionProperties = options.AllowedSessionProperties,
            AllowInlineFunctionDefinitions = options.AllowInlineFunctionDefinitions,
            RejectTimeTravelQueries = options.RejectTimeTravelQueries
        };
    }
}
