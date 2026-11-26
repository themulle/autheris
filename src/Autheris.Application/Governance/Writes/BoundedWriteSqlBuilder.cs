namespace Autheris.Application.Governance.Writes;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Parameterized SQL command built strictly from catalog-verified table and column identifiers, bound values, and the table's
/// compiled SELECT row-level security predicate. Never accepts client SQL text.
/// </summary>
public sealed record BuiltBoundedWriteCommand(
    string Sql,
    ImmutableArray<BoundedWriteParameter> Parameters,
    TargetSqlDialect Dialect,
    ParameterMarkerStyle MarkerStyle);

public sealed record BoundedWriteParameter(
    int Ordinal,
    string Marker,
    string ParameterName,
    object? Value);

/// <summary>
/// Builds dialect-specific, single-table parameterized SQL statements for Insert, Upsert (Databricks/SQL Server MERGE, PostgreSQL
/// ON CONFLICT DO UPDATE), UpdateByPk, and DeleteByPk, reusing the table's existing SELECT RLS predicate AST on every write target.
/// </summary>
public static class BoundedWriteSqlBuilder
{
    public static BuiltBoundedWriteCommand BuildInsert(
        TargetSqlDialect dialect,
        TableMetadata metadata,
        IReadOnlyList<KeyValuePair<string, object?>> orderedColumns)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(orderedColumns);
        if (orderedColumns.Count == 0)
        {
            throw new ArgumentException("At least one column is required for INSERT.", nameof(orderedColumns));
        }

        var style = DialectCapabilityTable.Default.Get(dialect).MarkerStyle;
        var parameters = new List<BoundedWriteParameter>(orderedColumns.Count);
        var cols = new List<string>(orderedColumns.Count);
        var vals = new List<string>(orderedColumns.Count);

        foreach (var (colName, value) in orderedColumns)
        {
            cols.Add(QuoteIdentifier(dialect, colName));
            vals.Add(AddParameter(parameters, dialect, style, value));
        }

        string target = QuoteTableName(dialect, metadata);
        string sql = $"INSERT INTO {target} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)})";
        return new BuiltBoundedWriteCommand(sql, parameters.ToImmutableArray(), dialect, style);
    }

    public static BuiltBoundedWriteCommand BuildUpdateByPk(
        TargetSqlDialect dialect,
        TableMetadata metadata,
        IReadOnlyList<KeyValuePair<string, object?>> pkValues,
        IReadOnlyList<KeyValuePair<string, object?>> patchValues,
        string? tenantColumn,
        TenantId tenantId,
        PolicyPredicate? rowPolicy)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (pkValues.Count == 0) throw new ArgumentException("Primary key values are required for UPDATE.", nameof(pkValues));
        if (patchValues.Count == 0) throw new ArgumentException("At least one patch column is required for UPDATE.", nameof(patchValues));

        var style = DialectCapabilityTable.Default.Get(dialect).MarkerStyle;
        var parameters = new List<BoundedWriteParameter>();
        var setParts = new List<string>(patchValues.Count);
        foreach (var (colName, value) in patchValues)
        {
            string marker = AddParameter(parameters, dialect, style, value);
            setParts.Add($"{QuoteIdentifier(dialect, colName)} = {marker}");
        }

        var whereParts = new List<string>(pkValues.Count + 2);
        foreach (var (pkCol, pkVal) in pkValues)
        {
            string marker = AddParameter(parameters, dialect, style, pkVal);
            whereParts.Add($"{QuoteIdentifier(dialect, pkCol)} = {marker}");
        }

        if (!string.IsNullOrWhiteSpace(tenantColumn))
        {
            string tenantMarker = AddParameter(parameters, dialect, style, tenantId.Value);
            whereParts.Add($"{QuoteIdentifier(dialect, tenantColumn)} = {tenantMarker}");
        }

        if (rowPolicy is not null)
        {
            string rlsSql = RenderRowPolicyPredicate(dialect, metadata, rowPolicy, targetAlias: null, parameters, style);
            whereParts.Add($"({rlsSql})");
        }

        string target = QuoteTableName(dialect, metadata);
        string sql = $"UPDATE {target} SET {string.Join(", ", setParts)} WHERE {string.Join(" AND ", whereParts)}";
        return new BuiltBoundedWriteCommand(sql, parameters.ToImmutableArray(), dialect, style);
    }

    public static BuiltBoundedWriteCommand BuildDeleteByPk(
        TargetSqlDialect dialect,
        TableMetadata metadata,
        IReadOnlyList<KeyValuePair<string, object?>> pkValues,
        string? tenantColumn,
        TenantId tenantId,
        PolicyPredicate? rowPolicy)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (pkValues.Count == 0) throw new ArgumentException("Primary key values are required for DELETE.", nameof(pkValues));

        var style = DialectCapabilityTable.Default.Get(dialect).MarkerStyle;
        var parameters = new List<BoundedWriteParameter>();
        var whereParts = new List<string>(pkValues.Count + 2);
        foreach (var (pkCol, pkVal) in pkValues)
        {
            string marker = AddParameter(parameters, dialect, style, pkVal);
            whereParts.Add($"{QuoteIdentifier(dialect, pkCol)} = {marker}");
        }

        if (!string.IsNullOrWhiteSpace(tenantColumn))
        {
            string tenantMarker = AddParameter(parameters, dialect, style, tenantId.Value);
            whereParts.Add($"{QuoteIdentifier(dialect, tenantColumn)} = {tenantMarker}");
        }

        if (rowPolicy is not null)
        {
            string rlsSql = RenderRowPolicyPredicate(dialect, metadata, rowPolicy, targetAlias: null, parameters, style);
            whereParts.Add($"({rlsSql})");
        }

        string target = QuoteTableName(dialect, metadata);
        string sql = $"DELETE FROM {target} WHERE {string.Join(" AND ", whereParts)}";
        return new BuiltBoundedWriteCommand(sql, parameters.ToImmutableArray(), dialect, style);
    }

    /// <summary>
    /// Builds a governed single-row UPSERT:
    /// - Databricks Delta: <c>MERGE INTO target AS t USING (SELECT ... ) AS s ON t.pk = s.pk [AND t.tenant = p] WHEN MATCHED [AND rls(t)] THEN UPDATE SET ... WHEN NOT MATCHED THEN INSERT (...) VALUES (...)</c>
    /// - SQL Server: <c>MERGE INTO target WITH (HOLDLOCK) AS t USING (SELECT ... ) AS s ON ... WHEN MATCHED [AND rls(t)] THEN UPDATE SET ... WHEN NOT MATCHED THEN INSERT (...) VALUES (...);</c>
    /// - PostgreSQL: <c>INSERT INTO target (...) VALUES (...) ON CONFLICT (pk) DO UPDATE SET ... WHERE [tenant AND] rls(target)</c>
    /// Note: callers MUST have already validated the candidate row against <see cref="TrinoSqlEngine.Ast.Security.PreExecutionRowCheck.ValidateRowMap"/>
    /// before executing, so the INSERT branch can never insert a row outside the caller's SELECT RLS scope.
    /// </summary>
    public static BuiltBoundedWriteCommand BuildUpsert(
        TargetSqlDialect dialect,
        TableMetadata metadata,
        IReadOnlyList<KeyValuePair<string, object?>> orderedColumns,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlySet<string> updateProtectedColumns,
        string? tenantColumn,
        TenantId tenantId,
        PolicyPredicate? rowPolicy)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(orderedColumns);
        ArgumentNullException.ThrowIfNull(primaryKeyColumns);
        if (orderedColumns.Count == 0) throw new ArgumentException("At least one column is required for UPSERT.", nameof(orderedColumns));
        if (primaryKeyColumns.Count == 0) throw new ArgumentException("Primary key columns are required for UPSERT.", nameof(primaryKeyColumns));

        return dialect switch
        {
            TargetSqlDialect.Databricks => BuildMergeUpsert(dialect, metadata, orderedColumns, primaryKeyColumns, updateProtectedColumns, tenantColumn, tenantId, rowPolicy, sqlServerHoldLock: false),
            TargetSqlDialect.SqlServer => BuildMergeUpsert(dialect, metadata, orderedColumns, primaryKeyColumns, updateProtectedColumns, tenantColumn, tenantId, rowPolicy, sqlServerHoldLock: true),
            TargetSqlDialect.PostgreSql => BuildPostgreSqlUpsert(metadata, orderedColumns, primaryKeyColumns, updateProtectedColumns, tenantColumn, tenantId, rowPolicy),
            _ => throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, $"governed upsert is not supported on dialect {dialect}")
        };
    }

    private static BuiltBoundedWriteCommand BuildMergeUpsert(
        TargetSqlDialect dialect,
        TableMetadata metadata,
        IReadOnlyList<KeyValuePair<string, object?>> orderedColumns,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlySet<string> updateProtectedColumns,
        string? tenantColumn,
        TenantId tenantId,
        PolicyPredicate? rowPolicy,
        bool sqlServerHoldLock)
    {
        var style = DialectCapabilityTable.Default.Get(dialect).MarkerStyle;
        var parameters = new List<BoundedWriteParameter>();
        var pkSet = new HashSet<string>(primaryKeyColumns, StringComparer.OrdinalIgnoreCase);

        var sourceProjections = new List<string>(orderedColumns.Count);
        var insertCols = new List<string>(orderedColumns.Count);
        var insertVals = new List<string>(orderedColumns.Count);
        var updateSets = new List<string>();

        string tAlias = QuoteIdentifier(dialect, "t");
        string sAlias = QuoteIdentifier(dialect, "s");

        foreach (var (colName, value) in orderedColumns)
        {
            string quotedCol = QuoteIdentifier(dialect, colName);
            string marker = AddParameter(parameters, dialect, style, value);
            sourceProjections.Add($"{marker} AS {quotedCol}");
            insertCols.Add(quotedCol);
            insertVals.Add($"{sAlias}.{quotedCol}");

            bool isPk = pkSet.Contains(colName);
            bool isTenant = !string.IsNullOrWhiteSpace(tenantColumn) && string.Equals(colName, tenantColumn, StringComparison.OrdinalIgnoreCase);
            bool isProtected = updateProtectedColumns.Contains(colName);
            if (!isPk && !isTenant && !isProtected)
            {
                updateSets.Add($"{tAlias}.{quotedCol} = {sAlias}.{quotedCol}");
            }
        }

        if (updateSets.Count == 0)
        {
            throw new SecurityException("UPSERT requires at least one updatable non-key, non-policy column.");
        }

        var onParts = new List<string>(primaryKeyColumns.Count + 1);
        foreach (var pk in primaryKeyColumns)
        {
            string quotedPk = QuoteIdentifier(dialect, pk);
            onParts.Add($"{tAlias}.{quotedPk} = {sAlias}.{quotedPk}");
        }

        if (!string.IsNullOrWhiteSpace(tenantColumn))
        {
            string tenantMarker = AddParameter(parameters, dialect, style, tenantId.Value);
            onParts.Add($"{tAlias}.{QuoteIdentifier(dialect, tenantColumn)} = {tenantMarker}");
        }

        string matchedClause = "WHEN MATCHED";
        if (rowPolicy is not null)
        {
            string rlsSql = RenderRowPolicyPredicate(dialect, metadata, rowPolicy, targetAlias: "t", parameters, style);
            matchedClause = $"WHEN MATCHED AND ({rlsSql})";
        }

        string target = QuoteTableName(dialect, metadata);
        string holdLock = sqlServerHoldLock ? " WITH (HOLDLOCK)" : string.Empty;
        string terminator = sqlServerHoldLock ? ";" : string.Empty;

        string sql =
            $"MERGE INTO {target}{holdLock} AS {tAlias} " +
            $"USING (SELECT {string.Join(", ", sourceProjections)}) AS {sAlias} " +
            $"ON {string.Join(" AND ", onParts)} " +
            $"{matchedClause} THEN UPDATE SET {string.Join(", ", updateSets)} " +
            $"WHEN NOT MATCHED THEN INSERT ({string.Join(", ", insertCols)}) VALUES ({string.Join(", ", insertVals)}){terminator}";

        return new BuiltBoundedWriteCommand(sql, parameters.ToImmutableArray(), dialect, style);
    }

    private static BuiltBoundedWriteCommand BuildPostgreSqlUpsert(
        TableMetadata metadata,
        IReadOnlyList<KeyValuePair<string, object?>> orderedColumns,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlySet<string> updateProtectedColumns,
        string? tenantColumn,
        TenantId tenantId,
        PolicyPredicate? rowPolicy)
    {
        const TargetSqlDialect dialect = TargetSqlDialect.PostgreSql;
        var style = DialectCapabilityTable.Default.Get(dialect).MarkerStyle;
        var parameters = new List<BoundedWriteParameter>();
        var pkSet = new HashSet<string>(primaryKeyColumns, StringComparer.OrdinalIgnoreCase);

        var insertCols = new List<string>(orderedColumns.Count);
        var insertVals = new List<string>(orderedColumns.Count);
        var updateSets = new List<string>();

        foreach (var (colName, value) in orderedColumns)
        {
            string quotedCol = QuoteIdentifier(dialect, colName);
            string marker = AddParameter(parameters, dialect, style, value);
            insertCols.Add(quotedCol);
            insertVals.Add(marker);

            bool isPk = pkSet.Contains(colName);
            bool isTenant = !string.IsNullOrWhiteSpace(tenantColumn) && string.Equals(colName, tenantColumn, StringComparison.OrdinalIgnoreCase);
            bool isProtected = updateProtectedColumns.Contains(colName);
            if (!isPk && !isTenant && !isProtected)
            {
                updateSets.Add($"{quotedCol} = EXCLUDED.{quotedCol}");
            }
        }

        if (updateSets.Count == 0)
        {
            throw new SecurityException("UPSERT requires at least one updatable non-key, non-policy column.");
        }

        var conflictCols = primaryKeyColumns.Select(pk => QuoteIdentifier(dialect, pk)).ToList();
        var whereParts = new List<string>(2);
        string tableQualifier = QuoteIdentifier(dialect, metadata.Identifier.TableName);

        if (!string.IsNullOrWhiteSpace(tenantColumn))
        {
            string tenantMarker = AddParameter(parameters, dialect, style, tenantId.Value);
            whereParts.Add($"{tableQualifier}.{QuoteIdentifier(dialect, tenantColumn)} = {tenantMarker}");
        }

        if (rowPolicy is not null)
        {
            string rlsSql = RenderRowPolicyPredicate(dialect, metadata, rowPolicy, targetAlias: metadata.Identifier.TableName, parameters, style);
            whereParts.Add($"({rlsSql})");
        }

        string target = QuoteTableName(dialect, metadata);
        string whereClause = whereParts.Count > 0 ? $" WHERE {string.Join(" AND ", whereParts)}" : string.Empty;
        string sql =
            $"INSERT INTO {target} ({string.Join(", ", insertCols)}) VALUES ({string.Join(", ", insertVals)}) " +
            $"ON CONFLICT ({string.Join(", ", conflictCols)}) DO UPDATE SET {string.Join(", ", updateSets)}{whereClause}";

        return new BuiltBoundedWriteCommand(sql, parameters.ToImmutableArray(), dialect, style);
    }

    private static string RenderRowPolicyPredicate(
        TargetSqlDialect dialect,
        TableMetadata metadata,
        PolicyPredicate rowPolicy,
        string? targetAlias,
        List<BoundedWriteParameter> targetParameters,
        ParameterMarkerStyle style)
    {
        Expression expr = targetAlias is not null
            ? QualifyColumns(rowPolicy.Expression, targetAlias)
            : rowPolicy.Expression;

        var tableName = BuildQualifiedTableName(metadata);
        var aliasIdent = targetAlias is not null ? new SqlIdentifier(targetAlias, IsQuoted: true) : null;
        var probe = new SelectStatement(
            null,
            new QuerySpecification(
                false,
                [new ColumnSelectItem(new LiteralExpression(1, LiteralType.Integer), null)],
                new NamedTableSource(tableName, aliasIdent),
                expr,
                null,
                null),
            null,
            null);

        var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
        var compiled = generator.Generate(probe, new ParameterSource(rowPolicy.Parameters, new Dictionary<string, object?>()));

        const string whereToken = " WHERE ";
        int whereIdx = compiled.Sql.IndexOf(whereToken, StringComparison.OrdinalIgnoreCase);
        if (whereIdx < 0)
        {
            throw new SecurityException("Failed to render row-level security predicate for bounded write.");
        }

        string rlsFragment = compiled.Sql[(whereIdx + whereToken.Length)..];

        // Parameter 0 is the probe's SELECT 1 literal; collect only parameters referenced in the WHERE clause.
        var whereParams = new List<(BoundParameter OldParam, string NewMarker)>();
        int offset = targetParameters.Count;
        foreach (var p in compiled.Parameters)
        {
            if (p.Origin == ParameterOrigin.QueryLiteral && p.Ordinal == 1 && Equals(p.Value, 1))
            {
                if (!ContainsMarker(rlsFragment, p.Marker))
                {
                    continue;
                }
            }

            if (!ContainsMarker(rlsFragment, p.Marker))
            {
                continue;
            }

            int newOrdinal = offset + whereParams.Count + 1;
            var (newMarker, newParamName) = FormatMarker(dialect, style, newOrdinal);
            whereParams.Add((p, newMarker));
            targetParameters.Add(new BoundedWriteParameter(newOrdinal, newMarker, newParamName, p.Value));
        }

        // Replace markers via placeholder tokens first so overlapping ordinals (e.g. $1 -> $2 -> $3) never collide.
        for (int i = 0; i < whereParams.Count; i++)
        {
            rlsFragment = ReplaceMarker(rlsFragment, whereParams[i].OldParam.Marker, $"__AUTHERIS_BIND_{i}__");
        }

        for (int i = 0; i < whereParams.Count; i++)
        {
            rlsFragment = rlsFragment.Replace($"__AUTHERIS_BIND_{i}__", whereParams[i].NewMarker, StringComparison.Ordinal);
        }

        return rlsFragment;
    }

    private static bool ContainsMarker(string sql, string marker)
    {
        if (string.IsNullOrEmpty(marker)) return false;
        return Regex.IsMatch(sql, Regex.Escape(marker) + @"(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
    }

    private static string ReplaceMarker(string sql, string marker, string replacement) =>
        Regex.Replace(sql, Regex.Escape(marker) + @"(?![A-Za-z0-9_])", replacement, RegexOptions.CultureInvariant);

    private static Expression QualifyColumns(Expression expr, string alias)
    {
        var rewriter = new AliasQualifyingRewriter(new SqlIdentifier(alias, IsQuoted: true));
        return (Expression)rewriter.Visit(expr);
    }

    private sealed class AliasQualifyingRewriter(SqlIdentifier alias) : TrinoSqlEngine.Ast.Visitors.SqlAstRewriter
    {
        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            if (node.Name.Parts.Count == 1)
            {
                return new ColumnReference(new SqlQualifiedName([alias, new SqlIdentifier(node.Name.SimpleName, IsQuoted: true)]));
            }

            return new ColumnReference(new SqlQualifiedName([alias, new SqlIdentifier(node.Name.SimpleName, IsQuoted: true)]));
        }
    }

    private static SqlQualifiedName BuildQualifiedTableName(TableMetadata metadata)
    {
        var parts = new List<SqlIdentifier>(3);
        if (!string.IsNullOrWhiteSpace(metadata.Table.PhysicalDatabaseName))
        {
            parts.Add(new SqlIdentifier(metadata.Table.PhysicalDatabaseName, IsQuoted: true));
        }

        if (!string.IsNullOrWhiteSpace(metadata.Identifier.Schema))
        {
            parts.Add(new SqlIdentifier(metadata.Identifier.Schema, IsQuoted: true));
        }

        parts.Add(new SqlIdentifier(metadata.Identifier.TableName, IsQuoted: true));
        return new SqlQualifiedName(parts);
    }

    private static string QuoteTableName(TargetSqlDialect dialect, TableMetadata metadata)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(metadata.Table.PhysicalDatabaseName))
        {
            parts.Add(QuoteIdentifier(dialect, metadata.Table.PhysicalDatabaseName));
        }

        if (!string.IsNullOrWhiteSpace(metadata.Identifier.Schema))
        {
            parts.Add(QuoteIdentifier(dialect, metadata.Identifier.Schema));
        }

        parts.Add(QuoteIdentifier(dialect, metadata.Identifier.TableName));
        return string.Join('.', parts);
    }

    public static string QuoteIdentifier(TargetSqlDialect dialect, string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (dialect == TargetSqlDialect.Databricks &&
            (identifier.Contains('$', StringComparison.Ordinal) ||
             identifier.Contains('{', StringComparison.Ordinal) ||
             identifier.Contains('}', StringComparison.Ordinal)))
        {
            throw new SecurityException("Databricks identifiers must not contain variable substitution characters.");
        }

        return dialect switch
        {
            TargetSqlDialect.Databricks => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`",
            TargetSqlDialect.SqlServer => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
            _ => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
        };
    }

    private static string AddParameter(
        List<BoundedWriteParameter> parameters,
        TargetSqlDialect dialect,
        ParameterMarkerStyle style,
        object? value)
    {
        int ordinal = parameters.Count + 1;
        var (marker, name) = FormatMarker(dialect, style, ordinal);
        parameters.Add(new BoundedWriteParameter(ordinal, marker, name, value));
        return marker;
    }

    private static (string Marker, string ParameterName) FormatMarker(
        TargetSqlDialect dialect,
        ParameterMarkerStyle style,
        int ordinal)
    {
        string ord = ordinal.ToString(CultureInfo.InvariantCulture);
        return style switch
        {
            ParameterMarkerStyle.AtNamedOrdinal => ($"@p{ord}", $"@p{ord}"),
            ParameterMarkerStyle.DollarOrdinal => dialect == TargetSqlDialect.PostgreSql
                ? ($"${ord}", string.Empty)
                : ($"${ord}", ord),
            ParameterMarkerStyle.ColonNamedOrdinal => ($":p{ord}", $"p{ord}"),
            ParameterMarkerStyle.ColonOrdinal => ($":{ord}", ord),
            ParameterMarkerStyle.QuestionOrdinal => ("?", string.Empty),
            _ => ($"@p{ord}", $"@p{ord}")
        };
    }
}
