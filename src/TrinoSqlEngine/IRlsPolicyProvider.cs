namespace TrinoSqlEngine;

using System;
using System.Collections.Generic;

public interface IRlsPolicyProvider
{
    bool ShouldApplyPolicy(string tableName);
    string GetPolicyFilter(string tableName);
}

public sealed class DefaultRlsPolicyProvider : IRlsPolicyProvider
{
    private readonly string _defaultFilter;
    private readonly Func<string, bool>? _predicate;
    private readonly Func<string, string>? _filterFunc;

    /// <summary>
    /// SQ-04: When true, matches unqualified simple name if qualified name does not match.
    /// Set to false in environments like GovernedSqlExecutionService to prevent cross-schema short-name collisions.
    /// </summary>
    public bool FallbackToSimpleName { get; set; } = true;

    public DefaultRlsPolicyProvider(
        string defaultFilter = "tenant_id = 42", 
        Func<string, bool>? predicate = null,
        Func<string, string>? filterFunc = null)
    {
        _defaultFilter = defaultFilter ?? throw new ArgumentNullException(nameof(defaultFilter));
        _predicate = predicate;
        _filterFunc = filterFunc;
    }

    public bool ShouldApplyPolicy(string tableName)
    {
        if (_predicate == null) return true;
        if (_predicate(tableName)) return true;

        if (FallbackToSimpleName)
        {
            string simpleName = SqlIdentifierHelper.GetSimpleName(tableName);
            if (!string.Equals(simpleName, tableName, StringComparison.Ordinal) && _predicate(simpleName)) return true;
        }

        return false;
    }

    public string GetPolicyFilter(string tableName)
    {
        if (_filterFunc != null) return _filterFunc(tableName);
        return _defaultFilter;
    }
}

public interface IColumnMaskingPolicyProvider
{
    bool HasMask(string tableName, string columnName);
    string GetMaskedExpression(string tableName, string columnName);
}

public sealed class DefaultColumnMaskingPolicyProvider : IColumnMaskingPolicyProvider
{
    private readonly Func<string, string, bool> _hasMaskPredicate;
    private readonly Func<string, string, string> _maskExpressionProvider;

    /// <summary>
    /// SQ-04: When true, matches unqualified simple name if qualified name does not match.
    /// Set to false in environments like GovernedSqlExecutionService to prevent cross-schema short-name collisions.
    /// </summary>
    public bool FallbackToSimpleName { get; set; } = true;

    public DefaultColumnMaskingPolicyProvider(
        Func<string, string, bool> hasMaskPredicate,
        Func<string, string, string> maskExpressionProvider)
    {
        _hasMaskPredicate = hasMaskPredicate ?? throw new ArgumentNullException(nameof(hasMaskPredicate));
        _maskExpressionProvider = maskExpressionProvider ?? throw new ArgumentNullException(nameof(maskExpressionProvider));
    }

    public bool HasMask(string tableName, string columnName)
    {
        if (_hasMaskPredicate(tableName, columnName)) return true;

        if (FallbackToSimpleName)
        {
            string simpleName = SqlIdentifierHelper.GetSimpleName(tableName);
            if (!string.Equals(simpleName, tableName, StringComparison.Ordinal) && _hasMaskPredicate(simpleName, columnName)) return true;
        }

        return false;
    }

    public string GetMaskedExpression(string tableName, string columnName)
    {
        if (FallbackToSimpleName)
        {
            string simpleName = SqlIdentifierHelper.GetSimpleName(tableName);
            if (!string.Equals(simpleName, tableName, StringComparison.Ordinal) && _hasMaskPredicate(simpleName, columnName))
            {
                return _maskExpressionProvider(simpleName, columnName);
            }
        }

        return _maskExpressionProvider(tableName, columnName);
    }
}

public sealed class RlsOptions
{
    /// <summary>
    /// Policy provider that determines whether and how a table is filtered.
    /// </summary>
    public IRlsPolicyProvider PolicyProvider { get; set; } = new DefaultRlsPolicyProvider();

    /// <summary>
    /// Explicit engine override: "AstCompiler", "LegacyTokenStream", or "ShadowDualRun".
    /// When null, defaults to <see cref="FastSqlEngine.SqlRewriterEngine"/>.
    /// </summary>
    public string? RewriterEngine { get; set; }

    /// <summary>
    /// Column masking provider that provides SQL masking expressions (e.g. 'NULL', '***', or hash).
    /// </summary>
    public IColumnMaskingPolicyProvider? ColumnMaskingProvider { get; set; }

    /// <summary>
    /// Callback returning the known column schema for a given table, enabling full in-database AST column pushdown.
    /// </summary>
    public Func<string, IReadOnlyList<string>?>? TableColumnsProvider { get; set; }

    /// <summary>
    /// Clamps or injects LIMIT {maxRows} on top-level queries to prevent result set exhaustion attacks (0 = disabled).
    /// </summary>
    public long EnforcedMaxRows { get; set; } = 0;

    /// <summary>
    /// SEC-01: When true, throws an exception if non-SELECT statements (INSERT, UPDATE, DELETE, DDL) are passed to the RLS rewriter.
    /// Default is true to maintain maximum security by default. Set to false to enable DML rewriting (UPDATE, DELETE, INSERT).
    /// </summary>
    public bool EnforceReadOnlyQueries { get; set; } = true;

    /// <summary>
    /// SEC-02: When true, automatically appends "AS {tableName}" if the rewritten subquery does not already have an alias.
    /// Default is false to maintain backward-compatibility with step-8 simple rewrite format.
    /// </summary>
    public bool AppendTableAlias { get; set; } = false;

    /// <summary>
    /// When true, validates INSERT and UPDATE statements against WITH CHECK OPTION to prevent tenant-hopping and trojan records.
    /// </summary>
    public bool EnforceWithCheckOption { get; set; } = true;

    /// <summary>
    /// The name of the tenant isolation column. Default is "tenant_id".
    /// </summary>
    public string TenantColumnName { get; set; } = "tenant_id";

    /// <summary>
    /// Expected tenant value for WITH CHECK OPTION verification. Null by default (must be explicitly set when WITH CHECK OPTION is active).
    /// </summary>
    public string? ExpectedTenantValue { get; set; } = null;

    /// <summary>
    /// If true, strictly forbids setting the tenant column in an UPDATE statement regardless of the assigned value.
    /// </summary>
    public bool DisallowTenantColumnModificationInUpdate { get; set; } = true;

    /// <summary>
    /// When true and EnforceWithCheckOption is true, requires INSERT statements to explicitly specify the tenant column.
    /// SEC M-23: Default is true (secure default). Set to false only for databases that enforce the tenant via DEFAULT/trigger.
    /// </summary>
    public bool RequireTenantColumnInInsert { get; set; } = true;

    /// <summary>
    /// SEC C-01: When true (default), every function call in the statement is checked against the function policy
    /// (<see cref="SqlFunctionPolicy"/>). Violations raise a <see cref="System.Security.SecurityException"/>.
    /// </summary>
    public bool EnforceFunctionPolicy { get; set; } = true;

    /// <summary>
    /// SEC C-01: Optional exclusive allowlist of (qualified, case-insensitive) function names.
    /// When set, only these functions are permitted. SEC P-02: the default denylist always wins, i.e. a denylisted
    /// function stays rejected even if it is allowlisted. See <see cref="SqlFunctionAllowlists"/> for curated defaults.
    /// </summary>
    public IReadOnlySet<string>? AllowedFunctions { get; set; }

    /// <summary>
    /// SEC C-01: Additional function names that are always rejected (also in allowlist mode).
    /// </summary>
    public IReadOnlySet<string>? AdditionalDeniedFunctions { get; set; }

    /// <summary>
    /// SEC H-14: Allowlist of (qualified, case-insensitive) table function names permitted in TABLE(...) invocations.
    /// Default null: all table function invocations are rejected.
    /// </summary>
    public IReadOnlySet<string>? AllowedTableFunctions { get; set; }

    /// <summary>
    /// SEC H-14: Allowlist of session property names permitted in WITH SESSION. Default null: WITH SESSION is rejected.
    /// </summary>
    public IReadOnlySet<string>? AllowedSessionProperties { get; set; }

    /// <summary>
    /// SEC H-14: When false (default), inline function definitions (WITH FUNCTION ...) are rejected.
    /// </summary>
    public bool AllowInlineFunctionDefinitions { get; set; } = false;

    /// <summary>
    /// SEC H-15: When true (default), UPDATE/DELETE statements referencing masked columns of the target table
    /// in SET or WHERE are rejected (prevents copy-out and row-count oracles on masked data).
    /// </summary>
    public bool RejectMaskedColumnsInDml { get; set; } = true;

    /// <summary>
    /// When true (default), UPDATE/DELETE statements without a WHERE clause, or with a trivially true WHERE clause
    /// (e.g. <c>WHERE 1=1</c>, <c>WHERE true</c>, <c>WHERE id = 5 OR 'a' = 'a'</c>), are rejected with an
    /// <see cref="UnfilteredDmlException"/>. The check runs on the original statement, independent of the WHERE
    /// clause injected by the RLS rewrite.
    /// </summary>
    public bool RejectUnfilteredDml { get; set; } = true;

    /// <summary>
    /// SQ-05: Target SQL database dialect for AST rewriting (ANSI, PostgreSQL, SQL Server, SQLite).
    /// </summary>
    public TargetSqlDialect TargetDialect { get; set; } = TargetSqlDialect.Ansi;

    /// <summary>
    /// SQ-02: When true (default, SEC P-06), comments are rejected in the input query to prevent comment-based dialect discrepancies.
    /// </summary>
    public bool RejectComments { get; set; } = true;

    /// <summary>
    /// SQ-01: When true (default), backslash escapes in string literals are rejected to prevent PostgreSQL E'...' / standard_conforming_strings lexer differentials.
    /// </summary>
    public bool RejectBackslashInStrings { get; set; } = true;

    /// <summary>
    /// SQ-01: When true (default), string type constructors like E'...' are rejected.
    /// </summary>
    public bool RejectEscapedStringLiterals { get; set; } = true;

    /// <summary>
    /// SQ-02: When true (default, SEC P-06), dollar-quoted strings ($$...$$) are rejected. Always rejected for SQL Server targets.
    /// </summary>
    public bool RejectDollarQuoting { get; set; } = true;

    /// <summary>
    /// SQL-1: When true, '[' / ']' tokens and literals/quoted identifiers containing '[', ']', '--' or '/*' are rejected.
    /// Always enforced for SQL Server and SQLite targets (they lex [...] as a quoted identifier). Default false so that
    /// PostgreSQL / ANSI targets keep array constructors and subscripts.
    /// </summary>
    public bool RejectBracketLexerDifferentials { get; set; } = false;

    /// <summary>
    /// SQ-07: When true (default), INSERT statements into tables that have custom row-level consent filters (beyond simple tenant isolation) are rejected.
    /// </summary>
    public bool RejectConsentFilteredInsert { get; set; } = true;

    /// <summary>
    /// SQ-07: Table names that have custom row-level consent filters (beyond simple tenant partition).
    /// Used by <see cref="RejectConsentFilteredInsert"/> to reject unauthorized INSERT statements.
    /// </summary>
    public HashSet<string> TablesWithConsentRowFilter { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SQ-03: When true (default), whole-row references (table or alias used as column/expression) in UPDATE/DELETE are rejected if the table has masked columns.
    /// </summary>
    public bool RejectWholeRowReferencesInDml { get; set; } = true;

    /// <summary>
    /// SQ-03: Table names that have masked columns. Used by <see cref="RejectWholeRowReferencesInDml"/> to reject unauthorized whole-row references.
    /// </summary>
    public HashSet<string> TablesWithMaskedColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SQ-10: When true (default, SEC P-06), unquoted identifiers with non-ASCII characters are rejected.
    /// </summary>
    public bool RejectNonAsciiIdentifiers { get; set; } = true;

    /// <summary>
    /// SQ-11: When true (default, SEC P-06), dots inside quoted identifiers are rejected.
    /// </summary>
    public bool RejectDotsInQuotedIdentifiers { get; set; } = true;

    /// <summary>
    /// SQ-13: When true (default, SEC P-06), time-travel syntax (FOR TIMESTAMP/VERSION AS OF) is rejected.
    /// </summary>
    public bool RejectTimeTravelQueries { get; set; } = true;
}

public enum TargetSqlDialect
{
    Ansi,
    PostgreSql,
    SqlServer,
    Sqlite,
    DuckDb,
    Snowflake
}

