namespace Autheris.Application.Sql.Policy;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Security;
using Autheris.Domain.Options;
using TrinoSqlEngine;

/// <summary>
/// WP-X0l: builds the immutable <see cref="CompileRequest"/> that replaces the mutable <c>RlsOptions</c> bag of
/// <c>GovernedSqlRewriter</c> (every switch of the former options has exactly one counterpart here, or is fixed by the compiler).
/// </summary>
public static class CompileRequestFactory
{
    /// <summary>
    /// The expansion factor of every governed compile (SEC-ADG-05; no option governs it). It is set explicitly on the request below and read
    /// by the key-set stage input (D-WPC-1), so the text budget of the stage and the compiler cannot drift. A test pins it to the engine default
    /// and to <see cref="CompileLimits.MaxExpansionFactorLimit"/>.
    /// </summary>
    public const int DefaultMaxExpansionFactor = 64;

    /// <param name="policy">The assembled policy; its DML guards must be <see cref="DmlGuardOptions.Strict"/> (CR-ADG-38).</param>
    /// <param name="options">The WebSQL options (validated here; the governed switches are not configurable per request).</param>
    /// <param name="isDml">The statement is DML: the DML statement classes are permitted (the dialect capability entry still decides).</param>
    /// <exception cref="SecurityException">The row policies of <paramref name="policy"/> were assembled for another dialect.</exception>
    /// <exception cref="SqlCompileConfigurationException">The policy carries DML guards other than <see cref="DmlGuardOptions.Strict"/>.</exception>
    public static CompileRequest Create(
        GovernancePolicy policy,
        TargetSqlDialect dialect,
        WebSqlOptions options,
        bool isDml,
        long enforcedMaxRows,
        IReadOnlySet<string> allowedFunctions)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allowedFunctions);
        ArgumentOutOfRangeException.ThrowIfNegative(enforcedMaxRows);

        // CR-ADG-38: the guards are not a per-request choice. They are never assigned here (production code must not); a policy that
        // carries another value is a configuration error, as it is for the compiler itself.
        if (policy.Dml is null || policy.Dml != DmlGuardOptions.Strict)
        {
            throw new SqlCompileConfigurationException();
        }

        if (policy.RowFilters is DecisionRowPolicyProvider { Dialect: { } policyDialect } && policyDialect != dialect)
        {
            throw new SecurityException("The row policies were assembled for another target dialect than the request.");
        }

        return new CompileRequest
        {
            TargetDialect = dialect,
            Policy = policy,
            TokenGuards = new SqlTokenSecurityOptions
            {
                RejectComments = true,
                RejectBackslashInStrings = true,
                RejectEscapedStringLiterals = true,
                RejectDollarQuoting = true,
                RejectBracketLexerDifferentials = dialect is TargetSqlDialect.SqlServer or TargetSqlDialect.Sqlite,
                RejectNonAsciiIdentifiers = true,
                RejectDotsInQuotedIdentifiers = true,
                RejectTimeTravelQueries = true,
                RejectReservedIdentifiers = true   // Phase 5 L3: the gateway prefix autheris_ is reserved in the user statement
            },
            Statements = isDml
                ? StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge
                : StatementPermissions.ReadOnly,
            EnforcedMaxRows = enforcedMaxRows,
            EnforceCatalogProjection = true,
            MaxExpansionFactor = DefaultMaxExpansionFactor,
            AllowedFunctions = allowedFunctions.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
        };
    }
}
