namespace Autheris.Application.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

public sealed partial class RowScopeCompiler
{
    private const int DefaultValuesBatchLimit = 1000;

    internal async ValueTask<PolicyPredicate> CompileKeySetFilterAsync(
        VirtualFilter filter,
        IReadOnlyDictionary<string, string>? columnMap,
        TableMetadata target,
        TenantId tenant,
        TargetSqlDialect dialect,
        string parameterPrefix,
        CancellationToken ct)
    {
        if (_keySetProvider == null)
        {
            throw new VirtualFilterBindingException(
                VirtualFilterErrorCodes.SourceUnavailable,
                $"KeySet evaluation for policy '{filter.Name}' requires an IVirtualFilterKeySetProvider.");
        }

        var mappedColumns = ResolveMappedTargetColumns(filter, columnMap, target);
        var keyTypes = mappedColumns
            .Select(c => VirtualFilterKeyCanonicalizer.ForColumnType(c.DataType, c.ColumnName))
            .ToArray();

        var keySet = await _keySetProvider.GetAsync(filter, keyTypes, tenant, ct).ConfigureAwait(false);
        return BuildKeySetPredicate(filter, mappedColumns, keySet, dialect, parameterPrefix);
    }

    internal static PolicyPredicate BuildKeySetPredicate(
        VirtualFilter filter,
        IReadOnlyList<TableColumn> mappedTargetColumns,
        VirtualFilterKeySet keySet,
        TargetSqlDialect dialect,
        string parameterPrefix)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(mappedTargetColumns);
        ArgumentNullException.ThrowIfNull(keySet);

        if (mappedTargetColumns.Count == 0 || mappedTargetColumns.Count != keySet.KeyTypes.Count)
        {
            throw new VirtualFilterBindingException(
                VirtualFilterErrorCodes.BindingMisconfigured,
                $"Key arity mismatch for policy '{filter.Name}'.");
        }

        var namer = new PolicyParameterNamer(parameterPrefix);
        var targetCols = mappedTargetColumns
            .Select(c => TargetQualifiedColumn(c.ColumnName))
            .ToArray();

        // Filter out any tuple with null/unrepresentable component (a NULL key column never matches).
        var validKeys = new List<VirtualFilterKey>(keySet.Keys.Count);
        foreach (var key in keySet.Keys)
        {
            if (IsCompleteNonNullKey(key, keySet.KeyTypes.Count))
            {
                validKeys.Add(key);
            }
        }

        if (validKeys.Count == 0)
        {
            return PolicyPredicate.DenyAll;
        }

        if (mappedTargetColumns.Count == 1)
        {
            var policyValues = validKeys
                .Select(k => ToPolicyValue(k[0]!, keySet.KeyTypes[0]))
                .ToList();
            var expr = AdvancedRlsPredicateBuilder.InLists(
                targetCols[0],
                policyValues,
                PolicyParameterNamer.InListLimit(dialect),
                namer);
            return PolicyPredicate.Create(expr, namer.Values);
        }

        var compositeExpr = BuildCompositeKeySetExpression(
            mappedTargetColumns,
            targetCols,
            keySet.KeyTypes,
            validKeys,
            dialect,
            namer);
        return PolicyPredicate.Create(compositeExpr, namer.Values);
    }

    private static Expression BuildCompositeKeySetExpression(
        IReadOnlyList<TableColumn> mappedTargetColumns,
        ColumnReference[] targetCols,
        IReadOnlyList<VirtualFilterKeyType> keyTypes,
        IReadOnlyList<VirtualFilterKey> keys,
        TargetSqlDialect dialect,
        PolicyParameterNamer namer)
    {
        int chunkSize = ResolveCompositeChunkSize(dialect);
        var chunks = new List<Expression>();
        for (int offset = 0; offset < keys.Count; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, keys.Count - offset);
            var rows = BuildRowValues(keys, offset, count, keyTypes, mappedTargetColumns, namer);
            chunks.Add(BuildCompositeChunk(mappedTargetColumns, targetCols, rows, dialect));
        }

        if (chunks.Count == 1)
        {
            return chunks[0];
        }

        var combined = chunks[0];
        for (int i = 1; i < chunks.Count; i++)
        {
            combined = new BinaryExpression(combined, BinaryOperator.Or, chunks[i]);
        }

        return combined;
    }

    private static int ResolveCompositeChunkSize(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.Oracle => PolicyParameterNamer.InListLimit(dialect) ?? DefaultValuesBatchLimit,
        TargetSqlDialect.SqlServer or TargetSqlDialect.Ansi => DefaultValuesBatchLimit,
        _ => int.MaxValue
    };

    private static List<RowValueExpression> BuildRowValues(
        IReadOnlyList<VirtualFilterKey> keys,
        int offset,
        int count,
        IReadOnlyList<VirtualFilterKeyType> keyTypes,
        IReadOnlyList<TableColumn> mappedTargetColumns,
        PolicyParameterNamer namer)
    {
        var rows = new List<RowValueExpression>(count);
        for (int i = 0; i < count; i++)
        {
            var key = keys[offset + i];
            var elements = new Expression[keyTypes.Count];
            for (int colIndex = 0; colIndex < keyTypes.Count; colIndex++)
            {
                var pVal = ToPolicyValue(key[colIndex]!, keyTypes[colIndex]);
                var paramExpr = namer.Add(pVal);
                if (!string.IsNullOrWhiteSpace(mappedTargetColumns[colIndex].DataType))
                {
                    paramExpr = paramExpr with { ColumnType = mappedTargetColumns[colIndex].DataType };
                }

                elements[colIndex] = paramExpr;
            }

            rows.Add(new RowValueExpression(elements));
        }

        return rows;
    }

    private static Expression BuildCompositeChunk(
        IReadOnlyList<TableColumn> mappedTargetColumns,
        ColumnReference[] targetCols,
        IReadOnlyList<RowValueExpression> rows,
        TargetSqlDialect dialect)
    {
        var rowOperand = new RowValueExpression(targetCols);
        switch (dialect)
        {
            case TargetSqlDialect.PostgreSql:
            case TargetSqlDialect.Oracle:
            case TargetSqlDialect.Databricks:
            case TargetSqlDialect.DuckDb:
                return new InListExpression(rowOperand, rows, IsNotIn: false);

            case TargetSqlDialect.Sqlite:
                return new InSubqueryExpression(
                    rowOperand,
                    new SelectStatement(null, new ValuesQueryBody(rows), null, null),
                    IsNotIn: false);

            case TargetSqlDialect.SqlServer:
            case TargetSqlDialect.Ansi:
                return BuildValuesExistsExpression(mappedTargetColumns, targetCols, rows);

            default:
                return new InListExpression(rowOperand, rows, IsNotIn: false);
        }
    }

    private static ExistsExpression BuildValuesExistsExpression(
        IReadOnlyList<TableColumn> mappedTargetColumns,
        ColumnReference[] targetCols,
        IReadOnlyList<RowValueExpression> rows)
    {
        const string valuesAlias = "v";
        var colAliases = new SqlIdentifier[mappedTargetColumns.Count];
        var joinPredicates = new Expression[mappedTargetColumns.Count];
        for (int i = 0; i < mappedTargetColumns.Count; i++)
        {
            var colName = "k" + (i + 1).ToString(CultureInfo.InvariantCulture);
            colAliases[i] = new SqlIdentifier(colName, true);
            var valCol = new ColumnReference(new SqlQualifiedName([new SqlIdentifier(valuesAlias, true), colAliases[i]]));
            joinPredicates[i] = new BinaryExpression(valCol, BinaryOperator.Equal, targetCols[i]);
        }

        Expression where = joinPredicates[0];
        for (int i = 1; i < joinPredicates.Length; i++)
        {
            where = new BinaryExpression(where, BinaryOperator.And, joinPredicates[i]);
        }

        var valuesSelect = new SelectStatement(null, new ValuesQueryBody(rows), null, null);
        var from = new SubqueryTableSource(valuesSelect, new SqlIdentifier(valuesAlias, true), colAliases);
        var spec = new QuerySpecification(
            Distinct: false,
            [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)],
            from,
            where,
            GroupBy: null,
            Having: null);
        return new ExistsExpression(new SelectStatement(null, spec, null, null));
    }

    private static IReadOnlyList<TableColumn> ResolveMappedTargetColumns(
        VirtualFilter filter,
        IReadOnlyDictionary<string, string>? columnMap,
        TableMetadata target)
    {
        var result = new List<TableColumn>(filter.TargetKeyColumns.Count);
        foreach (var keyCol in filter.TargetKeyColumns)
        {
            var targetName = columnMap != null && columnMap.TryGetValue(keyCol, out var mapped) ? mapped : keyCol;
            var col = target.GetColumn(targetName)
                ?? throw new VirtualFilterBindingException(
                    VirtualFilterErrorCodes.BindingMisconfigured,
                    $"Target table '{target.Identifier}' lacks column '{targetName}' required by policy '{filter.Name}'.");
            result.Add(col);
        }

        return result;
    }

    private static ColumnReference TargetQualifiedColumn(string columnName)
    {
        AdvancedRlsFilterGenerator.ValidateIdentifier(columnName, "ColumnName");
        return PolicyParameterNamer.Column($"{RowFilterAliases.Target}.{columnName}");
    }

    private static bool IsCompleteNonNullKey(VirtualFilterKey key, int expectedArity)
    {
        for (int i = 0; i < expectedArity; i++)
        {
            if (key[i] is null)
            {
                return false;
            }
        }

        return true;
    }

    private static PolicyValue ToPolicyValue(object value, VirtualFilterKeyType keyType) => keyType switch
    {
        VirtualFilterKeyType.IntegerNumber => new PolicyValue((long)value, SqlParameterType.Int64),
        VirtualFilterKeyType.DecimalNumber => new PolicyValue((decimal)value, SqlParameterType.Decimal),
        VirtualFilterKeyType.Uuid => new PolicyValue(((Guid)value).ToString("D"), SqlParameterType.String),
        VirtualFilterKeyType.Text => new PolicyValue((string)value, SqlParameterType.String),
        _ => throw new NotSupportedException($"Unsupported VirtualFilterKeyType '{keyType}'.")
    };
}
