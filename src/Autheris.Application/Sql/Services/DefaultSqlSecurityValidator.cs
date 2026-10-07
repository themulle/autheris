namespace Autheris.Application.Sql.Services;

using System;
using System.Text.RegularExpressions;
using Autheris.Application.Sql.Interfaces;

/// <summary>
/// Default implementation of <see cref="ISqlSecurityValidator"/> providing AST-level and token-level
/// security validation for SQL fragments, predicates, and identifiers.
/// </summary>
public sealed partial class DefaultSqlSecurityValidator : ISqlSecurityValidator
{
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex ValidIdentifierRegex();

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*(?:\.[a-zA-Z_][a-zA-Z0-9_]*)?(?:\s+(?:ASC|DESC))?(?:\s*,\s*[a-zA-Z_][a-zA-Z0-9_]*(?:\.[a-zA-Z_][a-zA-Z0-9_]*)?(?:\s+(?:ASC|DESC))?)*$", RegexOptions.IgnoreCase)]
    private static partial Regex ValidOrderByRegex();

    public void ValidatePredicateSql(string? predicate, string fieldName, bool allowSubqueries = true)
    {
        SqlSecurityValidator.ValidatePredicateSql(predicate, fieldName, allowSubqueries);
    }

    public void ValidateOrderBySql(string? orderBy, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
        {
            return;
        }

        if (orderBy.Length > 500)
        {
            throw new ArgumentException($"ORDER BY clause in '{fieldName}' exceeds maximum allowed length of 500 characters.", fieldName);
        }

        if (orderBy.Contains('\0') || orderBy.Contains(';') || orderBy.Contains("--") || orderBy.Contains("/*") || orderBy.Contains("*/") || orderBy.Contains("@@"))
        {
            throw new ArgumentException($"ORDER BY clause in '{fieldName}' contains prohibited characters or comment sequences.", fieldName);
        }

        if (!ValidOrderByRegex().IsMatch(orderBy.Trim()))
        {
            throw new ArgumentException($"ORDER BY clause in '{fieldName}' contains invalid column or direction syntax.", fieldName);
        }
    }

    public void ValidateParameterName(string? parameterName, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            throw new ArgumentException($"Parameter name in '{fieldName}' cannot be empty.", fieldName);
        }

        string trimmed = parameterName.TrimStart('@');
        if (!ValidIdentifierRegex().IsMatch(trimmed))
        {
            throw new ArgumentException($"Parameter name '{parameterName}' in '{fieldName}' contains invalid identifier characters.", fieldName);
        }
    }
}
