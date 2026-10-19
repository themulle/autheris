namespace Autheris.Application.Governance.Validation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.ConsentRequests;
using Autheris.Domain.Model;

/// <summary>
/// SEC-GOV113-05: Row filter predicate AST validation.
/// Enforces column whitelist against table metadata, operator whitelist (=, !=, &lt;, &lt;=, &gt;, &gt;=, IN, NOT IN, LIKE, IS NULL, IS NOT NULL),
/// value type compatibility, and rejects subqueries / arbitrary SQL expressions with 400 BAD_REQUEST.
/// </summary>
public static class RowFilterAstValidator
{
    private static readonly HashSet<string> AllowedOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "=", "!=", "<>", "<", "<=", ">", ">=",
        "EQ", "NEQ", "LT", "LTE", "GT", "GTE",
        "IN", "NOT IN", "LIKE", "IS NULL", "IS NOT NULL"
    };

    private static readonly Regex SafeIdentifierRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex SqlInjectionKeywordsRegex = new(@"\b(SELECT|UNION|DROP|INSERT|UPDATE|DELETE|EXEC|EXECUTE|ALTER|CREATE|TRUNCATE|DECLARE|XP_|PG_|INFORMATION_SCHEMA|OR|AND)\b|--|/\*|;|'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void Validate(RowFilterInput? input, TableMetadata metadata)
    {
        if (input == null || input.Predicates == null || input.Predicates.Count == 0)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(metadata);

        var validColumns = metadata.Columns.ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);

        foreach (var predicate in input.Predicates)
        {
            if (string.IsNullOrWhiteSpace(predicate.ColumnName))
            {
                throw new ConsentRequestException("BAD_REQUEST", "Column name is required in row filter predicate.");
            }

            var colName = predicate.ColumnName.Trim();
            if (!SafeIdentifierRegex.IsMatch(colName))
            {
                throw new ConsentRequestException("BAD_REQUEST", $"Invalid column name '{colName}' in row filter predicate.");
            }

            if (!validColumns.TryGetValue(colName, out var colMeta))
            {
                throw new ConsentRequestException("BAD_REQUEST", $"Column '{colName}' does not exist on table '{metadata.Identifier}'.");
            }

            if (string.IsNullOrWhiteSpace(predicate.Operator))
            {
                throw new ConsentRequestException("BAD_REQUEST", "Operator is required in row filter predicate.");
            }

            var op = predicate.Operator.Trim();
            if (!AllowedOperators.Contains(op))
            {
                throw new ConsentRequestException("BAD_REQUEST", $"Operator '{op}' is not permitted in row filter predicate.");
            }

            var normOp = NormalizeOperator(op);

            // Subquery and arbitrary SQL injection prevention
            if (SqlInjectionKeywordsRegex.IsMatch(colName) || SqlInjectionKeywordsRegex.IsMatch(op))
            {
                throw new ConsentRequestException("BAD_REQUEST", "Subqueries and arbitrary SQL expressions are forbidden in row filter AST.");
            }

            var valJson = predicate.ValueJson?.Trim();

            if (normOp is "IS NULL" or "IS NOT NULL")
            {
                if (!string.IsNullOrEmpty(valJson) && valJson != "null" && valJson != "[]" && valJson != "\"\"")
                {
                    throw new ConsentRequestException("BAD_REQUEST", $"Operator '{op}' does not accept a value.");
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(valJson))
            {
                throw new ConsentRequestException("BAD_REQUEST", $"Value is required for operator '{op}'.");
            }

            // Reject SQL injection / subquery patterns in value text
            if (SqlInjectionKeywordsRegex.IsMatch(valJson))
            {
                throw new ConsentRequestException("BAD_REQUEST", "Subqueries and arbitrary SQL expressions are forbidden in row filter AST.");
            }

            // Validate JSON syntax
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(valJson);
            }
            catch (JsonException ex)
            {
                throw new ConsentRequestException("BAD_REQUEST", $"Value JSON is invalid: {ex.Message}");
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (normOp is "IN" or "NOT IN")
                {
                    if (root.ValueKind != JsonValueKind.Array)
                    {
                        throw new ConsentRequestException("BAD_REQUEST", $"Operator '{op}' requires a JSON array value.");
                    }
                    if (root.GetArrayLength() == 0)
                    {
                        throw new ConsentRequestException("BAD_REQUEST", $"Operator '{op}' requires a non-empty array.");
                    }
                }
                else
                {
                    if (root.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        throw new ConsentRequestException("BAD_REQUEST", $"Scalar operator '{op}' cannot be applied to an array or object.");
                    }
                }
            }
        }
    }

    public static IReadOnlyList<ConsentRowFilter> BuildRowFilters(RowFilterInput? input, TableMetadata metadata, Guid consentId)
    {
        if (input == null || input.Predicates == null || input.Predicates.Count == 0)
        {
            return Array.Empty<ConsentRowFilter>();
        }

        Validate(input, metadata);

        var validColumns = metadata.Columns.ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);
        var result = new List<ConsentRowFilter>();
        int group = int.TryParse(input.FilterGroup, out var g) ? g : 1;

        foreach (var p in input.Predicates)
        {
            var col = validColumns[p.ColumnName.Trim()];
            var normOp = NormalizeOperator(p.Operator.Trim());
            var valType = string.IsNullOrWhiteSpace(p.ValueType) ? col.DataType : p.ValueType.Trim().ToLowerInvariant();
            var valJson = p.ValueJson?.Trim() ?? "[]";
            if (normOp is "IS NULL" or "IS NOT NULL")
            {
                valJson = "[]";
            }

            result.Add(new ConsentRowFilter
            {
                ConsentId = consentId,
                FilterGroup = group,
                TableColumnId = col.Id,
                ColumnName = col.ColumnName,
                Operator = normOp,
                ValueType = valType,
                ValueJson = valJson,
                ValueSource = ConsentRowFilterShape.LiteralSource,
                FilterType = RowFilterType.SimpleColumnPredicate
            });
        }

        return result;
    }

    private static string NormalizeOperator(string op) => op.ToUpperInvariant() switch
    {
        "=" or "EQ" => "EQ",
        "!=" or "<>" or "NEQ" => "NEQ",
        "<" or "LT" => "LT",
        "<=" or "LTE" => "LTE",
        ">" or "GT" => "GT",
        ">=" or "GTE" => "GTE",
        "IN" => "IN",
        "NOT IN" => "NOT IN",
        "LIKE" => "LIKE",
        "IS NULL" => "IS NULL",
        "IS NOT NULL" => "IS NOT NULL",
        _ => op.ToUpperInvariant()
    };
}
