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

namespace TrinoSqlEngine.Ast.Visitors;

public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    private void EnsureNoMaskedColumnReferences(string normalizedTableName, string? targetAlias, SqlNode scope, string clause)
    {
        if (!_options.RejectMaskedColumnsInDml || _options.ColumnMaskingProvider == null)
            return;

        CheckNoMaskedColumnReferences(normalizedTableName, targetAlias, scope, clause);
    }

    private void EnsureNoMaskedColumnReferencesInPredicate(string normalizedTableName, string? targetAlias, SqlNode scope, string clause)
    {
        if (!_options.RejectMaskedColumnsInPredicates || _options.ColumnMaskingProvider == null)
            return;

        CheckNoMaskedColumnReferences(normalizedTableName, targetAlias, scope, clause);
    }

    private void CheckNoMaskedColumnReferences(string normalizedTableName, string? targetAlias, SqlNode scope, string clause)
    {
        if (_options.ColumnMaskingProvider == null)
            return;

        string simpleTableName = SqlIdentifierHelper.GetSimpleName(normalizedTableName);
        var stack = new Stack<SqlNode>();
        stack.Push(scope);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ColumnReference colRef)
            {
                string colName = colRef.Name.SimpleName;
                bool isMasked = _options.ColumnMaskingProvider.HasMask(normalizedTableName, colName);

                if (!isMasked && colRef.Name.Parts.Count > 1)
                {
                    string qualifier = colRef.Name.Parts[^2].Value;
                    isMasked = _options.ColumnMaskingProvider.HasMask(qualifier, colName);
                }

                if (!isMasked && _options.TablesWithMaskedColumns != null)
                {
                    foreach (var tbl in _options.TablesWithMaskedColumns)
                    {
                        if (_options.ColumnMaskingProvider.HasMask(tbl, colName))
                        {
                            isMasked = true;
                            break;
                        }
                    }
                }

                if (isMasked)
                {
                    throw new SecurityException($"Masked column '{colName}' of table '{normalizedTableName}' must not be referenced in {clause}.");
                }

                if (_options.RejectWholeRowReferencesInDml &&
                    (colName.Equals(normalizedTableName, StringComparison.OrdinalIgnoreCase) ||
                     colName.Equals(simpleTableName, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrEmpty(targetAlias) && colName.Equals(targetAlias, StringComparison.OrdinalIgnoreCase))) &&
                    HasMaskingForTable(normalizedTableName))
                {
                    throw new SecurityException($"Whole-row reference to '{colName}' in {clause} is forbidden because table '{normalizedTableName}' contains masked columns.");
                }
            }
            else if (node is UsingJoinCondition usingCond)
            {
                foreach (var col in usingCond.Columns)
                {
                    string colName = col.Value;
                    bool isMasked = _options.ColumnMaskingProvider.HasMask(normalizedTableName, colName);
                    if (!isMasked && _options.TablesWithMaskedColumns != null)
                    {
                        foreach (var tbl in _options.TablesWithMaskedColumns)
                        {
                            if (_options.ColumnMaskingProvider.HasMask(tbl, colName))
                            {
                                isMasked = true;
                                break;
                            }
                        }
                    }
                    if (isMasked)
                    {
                        throw new SecurityException($"Masked column '{colName}' of table '{normalizedTableName}' must not be referenced in {clause}.");
                    }
                }
            }
            else if (node is WildcardSelectItem wildcard)
            {
                bool hasTableMasking = HasMaskingForTable(normalizedTableName);
                if (wildcard.Qualifier != null)
                {
                    string qualSimple = wildcard.Qualifier.SimpleName;
                    string qualNorm = wildcard.Qualifier.NormalizedName;
                    bool matchesTarget = qualSimple.Equals(simpleTableName, StringComparison.OrdinalIgnoreCase) ||
                                         qualNorm.Equals(normalizedTableName, StringComparison.OrdinalIgnoreCase) ||
                                         (!string.IsNullOrEmpty(targetAlias) &&
                                          (qualSimple.Equals(targetAlias, StringComparison.OrdinalIgnoreCase) ||
                                           qualNorm.Equals(targetAlias, StringComparison.OrdinalIgnoreCase)));

                    if ((matchesTarget && hasTableMasking) ||
                        HasMaskingForTable(qualNorm) ||
                        HasMaskingForTable(qualSimple))
                    {
                        throw new SecurityException($"Whole-row reference to '{wildcard.Qualifier}.*' in {clause} is forbidden because table '{normalizedTableName}' contains masked columns.");
                    }
                }
            }

            // Push children
            PushChildren(node, stack);
        }
    }

    private static void CollectTableSources(QueryBody? body, List<(string TableName, string? Alias)> tables)
    {
        if (body == null) return;
        switch (body)
        {
            case QuerySpecification qs:
                CollectTableSources(qs.From, tables);
                break;
            case SetOperationQuery so:
                CollectTableSources(so.Left, tables);
                CollectTableSources(so.Right, tables);
                break;
            case TableQueryBody tq:
                tables.Add((tq.TableName.NormalizedName, null));
                break;
        }
    }

    private static void CollectTableSources(TableSource? from, List<(string TableName, string? Alias)> tables)
    {
        if (from == null) return;
        switch (from)
        {
            case NamedTableSource named:
                tables.Add((named.Name.NormalizedName, named.Alias?.Value));
                break;
            case JoinedTableSource joined:
                CollectTableSources(joined.Left, tables);
                CollectTableSources(joined.Right, tables);
                break;
            case LateralTableSource lateral:
                CollectTableSources(lateral.Subquery.Body, tables);
                break;
        }
    }

    private void CheckJoinConditions(TableSource? from, List<(string TableName, string? Alias)> tables)
    {
        if (from is JoinedTableSource j)
        {
            if (j.Condition is OnJoinCondition on)
            {
                foreach (var (tbl, alias) in tables)
                {
                    EnsureNoMaskedColumnReferencesInPredicate(tbl, alias, on.Predicate, "JOIN condition");
                }
            }
            else if (j.Condition is UsingJoinCondition usingCond)
            {
                foreach (var (tbl, alias) in tables)
                {
                    EnsureNoMaskedColumnReferencesInPredicate(tbl, alias, usingCond, "JOIN condition");
                }
            }
            CheckJoinConditions(j.Left, tables);
            CheckJoinConditions(j.Right, tables);
        }
    }

    /// <summary>
    /// R-53 / B-06 / Section 4: Generates dialect-specific SQL expressions for advanced masking rules:
    /// - GEO_JITTER: CASE WHEN IS NULL OR 0.0 THEN ... ELSE ROUND(...) END
    /// - PARTIAL_MASK: CASE WHEN IS NULL THEN NULL WHEN LEN(...) &lt;= ... THEN '*****' ELSE CONCAT(...) END
    /// - REDACT: typgerechtes CAST(NULL AS ...) for numeric/temporal/boolean, or '[REDACTED]' for text
    /// </summary>
    public static string BuildDialectMaskExpression(
        string columnName,
        string ruleType,
        TargetSqlDialect dialect,
        string? dataType = null,
        int decimals = 2,
        int keepPrefix = 1,
        int keepSuffix = 0,
        char maskChar = '*',
        bool fixedLength = false,
        string? replacement = null)
    {
        var normRule = (ruleType ?? "REDACT").Trim().ToUpperInvariant();
        var isSqlServer = dialect == TargetSqlDialect.SqlServer;
        var quotedCol = isSqlServer
            ? $"[{columnName.Replace("]", "]]")}]"
            : $"\"{columnName.Replace("\"", "\"\"")}\"";

        switch (normRule)
        {
            case "GEO_JITTER":
            {
                var dec = Math.Clamp(decimals, 0, 6);
                if (isSqlServer)
                {
                    return $"CASE WHEN {quotedCol} IS NULL OR {quotedCol} = 0.0 THEN {quotedCol} ELSE ROUND({quotedCol}, {dec}) END";
                }
                if (dialect is TargetSqlDialect.PostgreSql or TargetSqlDialect.DuckDb)
                {
                    return $"CASE WHEN {quotedCol} IS NULL OR {quotedCol} = 0.0 THEN {quotedCol} ELSE ROUND({quotedCol}::numeric, {dec})::double precision END";
                }
                return $"CASE WHEN {quotedCol} IS NULL OR {quotedCol} = 0.0 THEN {quotedCol} ELSE ROUND({quotedCol}, {dec}) END";
            }

            case "PARTIAL_MASK":
            {
                var p = Math.Max(0, keepPrefix);
                var s = Math.Max(0, keepSuffix);
                var maskStr = fixedLength ? new string(maskChar, 5) : (p + s >= 0 ? new string(maskChar, 5) : "*****");

                if (isSqlServer)
                {
                    if (s > 0)
                    {
                        var maskPart = fixedLength
                            ? $"'{maskStr}'"
                            : $"REPLICATE('{maskChar}', CASE WHEN LEN({quotedCol}) > {p + s} THEN LEN({quotedCol}) - {p + s} ELSE 5 END)";
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LEN({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE CONCAT(LEFT({quotedCol}, {p}), {maskPart}, RIGHT({quotedCol}, {s})) END";
                    }
                    else
                    {
                        var maskPart = fixedLength
                            ? $"'{maskStr}'"
                            : $"REPLICATE('{maskChar}', CASE WHEN LEN({quotedCol}) > {p + s} THEN LEN({quotedCol}) - {p + s} ELSE 5 END)";
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LEN({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE CONCAT(LEFT({quotedCol}, {p}), {maskPart}) END";
                    }
                }
                else if (dialect == TargetSqlDialect.Sqlite)
                {
                    if (s > 0)
                    {
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LENGTH({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE SUBSTR({quotedCol}, 1, {p}) || '{maskStr}' || SUBSTR({quotedCol}, -{s}) END";
                    }
                    else
                    {
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LENGTH({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE SUBSTR({quotedCol}, 1, {p}) || '{maskStr}' END";
                    }
                }
                else // PostgreSql, DuckDb, Trino, Ansi
                {
                    if (s > 0)
                    {
                        var maskPart = fixedLength
                            ? $"'{maskStr}'"
                            : $"REPEAT('{maskChar}', CASE WHEN LENGTH({quotedCol}) > {p + s} THEN LENGTH({quotedCol}) - {p + s} ELSE 5 END)";
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LENGTH({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE CONCAT(SUBSTRING({quotedCol} FROM 1 FOR {p}), {maskPart}, SUBSTRING({quotedCol} FROM LENGTH({quotedCol}) - {s - 1} FOR {s})) END";
                    }
                    else
                    {
                        var maskPart = fixedLength
                            ? $"'{maskStr}'"
                            : $"REPEAT('{maskChar}', CASE WHEN LENGTH({quotedCol}) > {p + s} THEN LENGTH({quotedCol}) - {p + s} ELSE 5 END)";
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LENGTH({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE CONCAT(SUBSTRING({quotedCol} FROM 1 FOR {p}), {maskPart}) END";
                    }
                }
            }

            case "REDACT":
            {
                if (!string.IsNullOrWhiteSpace(dataType) && IsNumericOrTemporalType(dataType))
                {
                    var castType = MapToDialectTypeName(dataType, dialect);
                    return $"CAST(NULL AS {castType})";
                }
                var prefix = isSqlServer ? "N" : "";
                var repl = replacement ?? "[REDACTED]";
                return $"{prefix}'{repl.Replace("'", "''")}'";
            }

            case "NULLIFY":
                return "NULL";

            default:
                if (!string.IsNullOrWhiteSpace(dataType) && IsNumericOrTemporalType(dataType))
                {
                    var castType = MapToDialectTypeName(dataType, dialect);
                    return $"CAST(NULL AS {castType})";
                }
                var dPrefix = isSqlServer ? "N" : "";
                var dRepl = replacement ?? "[REDACTED]";
                return $"{dPrefix}'{dRepl.Replace("'", "''")}'";
        }
    }

    private static bool IsNumericOrTemporalType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return false;
        var dt = dataType.Trim().ToLowerInvariant();
        if (dt.Contains('(')) dt = dt[..dt.IndexOf('(')].Trim();
        return dt is "int" or "integer" or "bigint" or "smallint" or "tinyint" or "numeric" or "decimal"
            or "money" or "smallmoney" or "real" or "float" or "double precision" or "double"
            or "bit" or "bool" or "boolean" or "date" or "datetime" or "datetime2" or "smalldatetime"
            or "timestamp" or "timestamptz" or "time" or "uniqueidentifier" or "uuid";
    }

    private static string MapToDialectTypeName(string dataType, TargetSqlDialect dialect)
    {
        var dt = dataType.Trim().ToLowerInvariant();
        var orig = dataType.Trim();
        if (dt.Contains('(')) dt = dt[..dt.IndexOf('(')].Trim();

        return dialect switch
        {
            TargetSqlDialect.SqlServer => dt switch
            {
                "int" or "integer" => "INT",
                "bigint" => "BIGINT",
                "smallint" => "SMALLINT",
                "tinyint" => "TINYINT",
                "decimal" or "numeric" => orig.Contains('(') ? orig.ToUpperInvariant() : "DECIMAL(18, 4)",
                "float" or "double" or "real" => "FLOAT",
                "bit" or "bool" or "boolean" => "BIT",
                "date" => "DATE",
                "datetime" or "datetime2" or "timestamp" => "DATETIME2",
                "uniqueidentifier" or "uuid" => "UNIQUEIDENTIFIER",
                _ => "DECIMAL(18, 4)"
            },
            TargetSqlDialect.PostgreSql or TargetSqlDialect.DuckDb => dt switch
            {
                "int" or "integer" => "INTEGER",
                "bigint" => "BIGINT",
                "smallint" or "tinyint" => "SMALLINT",
                "decimal" or "numeric" => "NUMERIC",
                "float" or "real" => "REAL",
                "double" or "double precision" => "DOUBLE PRECISION",
                "bit" or "bool" or "boolean" => "BOOLEAN",
                "date" => "DATE",
                "datetime" or "datetime2" or "timestamp" or "timestamptz" => "TIMESTAMP",
                "uuid" or "uniqueidentifier" => "UUID",
                _ => "NUMERIC"
            },
            TargetSqlDialect.Sqlite => dt switch
            {
                "int" or "integer" or "bigint" or "smallint" or "tinyint" => "INTEGER",
                _ => "NUMERIC"
            },
            _ => dt switch
            {
                "int" or "integer" => "INTEGER",
                "bigint" => "BIGINT",
                "decimal" or "numeric" => "NUMERIC",
                "float" or "double" => "DOUBLE PRECISION",
                "date" => "DATE",
                "datetime" or "timestamp" => "TIMESTAMP",
                "bool" or "boolean" => "BOOLEAN",
                _ => "NUMERIC"
            }
        };
    }
}
