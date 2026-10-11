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
    /// <param name="policy">The assembled policy; its DML guards are replaced by <see cref="DmlGuardOptions.Strict"/> (CR-ADG-38).</param>
    /// <param name="options">The WebSQL options (validated here; the governed switches are not configurable per request).</param>
    /// <param name="isDml">The statement is DML: the DML statement classes are permitted (the dialect capability entry still decides).</param>
    /// <exception cref="SecurityException">The row policies of <paramref name="policy"/> were assembled for another dialect.</exception>
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

        if (policy.RowFilters is DecisionRowPolicyProvider { Dialect: { } policyDialect } && policyDialect != dialect)
        {
            throw new SecurityException("The row policies were assembled for another target dialect than the request.");
        }

        return new CompileRequest
        {
            TargetDialect = dialect,
            Policy = policy with { Dml = DmlGuardOptions.Strict },
            TokenGuards = new SqlTokenSecurityOptions
            {
                RejectComments = true,
                RejectBackslashInStrings = true,
                RejectEscapedStringLiterals = true,
                RejectDollarQuoting = dialect != TargetSqlDialect.PostgreSql,
                RejectBracketLexerDifferentials = dialect is TargetSqlDialect.SqlServer or TargetSqlDialect.Sqlite,
                RejectNonAsciiIdentifiers = true,
                RejectDotsInQuotedIdentifiers = true,
                RejectTimeTravelQueries = true
            },
            Statements = isDml
                ? StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge
                : StatementPermissions.ReadOnly,
            EnforcedMaxRows = enforcedMaxRows,
            EnforceCatalogProjection = true,
            AllowedFunctions = allowedFunctions.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
        };
    }
}
