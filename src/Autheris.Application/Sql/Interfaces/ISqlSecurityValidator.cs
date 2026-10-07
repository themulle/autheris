namespace Autheris.Application.Sql.Interfaces;

/// <summary>
/// Abstraction for validating SQL fragments, filters, predicates, and RLS expressions.
/// Enforces Zero-Trust principles using AST parsing to prevent SQL injection, query stacking,
/// comment breakouts, and RLS bypasses in compiled AST and runtime data source queries.
/// </summary>
public interface ISqlSecurityValidator
{
    /// <summary>
    /// Validates a SQL predicate or filter expression to ensure it is free from SQL injection,
    /// statement terminators, comment breakouts, and unbalanced syntax.
    /// </summary>
    void ValidatePredicateSql(string? predicate, string fieldName, bool allowSubqueries = true);

    /// <summary>
    /// Validates an ORDER BY SQL fragment to ensure only valid column expressions and sort directions are present.
    /// </summary>
    void ValidateOrderBySql(string? orderBy, string fieldName);

    /// <summary>
    /// Validates a parameter identifier or name for safe inclusion in SQL queries.
    /// </summary>
    void ValidateParameterName(string? parameterName, string fieldName);
}
