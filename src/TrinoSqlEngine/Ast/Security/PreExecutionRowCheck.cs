namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Compiles and evaluates a table's existing SELECT row-level security predicate in memory against candidate INSERT or MERGE rows
/// before any command reaches a dialect without transactional rollback or INSERT row counts (such as Databricks Delta).
/// The compiled representation is value-free so it can live safely inside <see cref="CompiledSqlTemplate"/> and rebind policy values per caller.
/// Any unsupported AST node fails closed at compile time with <see cref="SqlCompileNotSupportedException"/>.
/// </summary>
public sealed class PreExecutionRowCheck
{
    private readonly Expression _policyPredicate;
    private readonly ImmutableHashSet<string> _policyColumns;
    private readonly ImmutableArray<string> _writtenColumns;
    private readonly ImmutableDictionary<string, int> _columnIndices;
    private readonly ImmutableArray<ImmutableArray<Expression>> _rows;

    private PreExecutionRowCheck(
        Expression policyPredicate,
        ImmutableHashSet<string> policyColumns,
        ImmutableArray<string> writtenColumns,
        ImmutableDictionary<string, int> columnIndices,
        ImmutableArray<ImmutableArray<Expression>> rows)
    {
        _policyPredicate = policyPredicate;
        _policyColumns = policyColumns;
        _writtenColumns = writtenColumns;
        _columnIndices = columnIndices;
        _rows = rows;
    }

    public int RowCount => _rows.Length;
    public ImmutableHashSet<string> PolicyColumns => _policyColumns;

    /// <summary>
    /// Compiles a pre-execution check for an <c>INSERT ... VALUES</c> statement. Fails closed at compile time if the policy references
    /// any column not supplied by the INSERT, or if the policy predicate or any policy-column cell uses an unsupported AST construct.
    /// </summary>
    public static PreExecutionRowCheck Compile(
        Expression policyPredicate,
        IReadOnlySet<string> policyColumns,
        IReadOnlyList<SqlIdentifier> writtenColumns,
        IReadOnlyList<RowValueExpression> rows)
    {
        ArgumentNullException.ThrowIfNull(policyPredicate);
        ArgumentNullException.ThrowIfNull(policyColumns);
        ArgumentNullException.ThrowIfNull(writtenColumns);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            throw new SecurityException("An INSERT VALUES clause without rows cannot be verified.");
        }

        var writtenArray = writtenColumns.Select(static c => c.Value).ToImmutableArray();
        var indices = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < writtenArray.Length; i++)
        {
            indices[writtenArray[i]] = i;
        }

        var indexMap = indices.ToImmutable();
        var normalizedPolicyCols = policyColumns.Select(static c => c.ToLowerInvariant()).ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var col in normalizedPolicyCols)
        {
            if (!indexMap.ContainsKey(col))
            {
                throw new SecurityException("An INSERT into a table with a row-level policy must supply every column the policy references.");
            }
        }

        ValidatePolicyAst(policyPredicate, indexMap);

        var rowBuilder = ImmutableArray.CreateBuilder<ImmutableArray<Expression>>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Elements.Count != writtenArray.Length)
            {
                throw new SecurityException("The number of inserted values does not match the column list.");
            }

            foreach (var col in normalizedPolicyCols)
            {
                int colIndex = indexMap[col];
                ValidateCellAst(row.Elements[colIndex]);
            }

            rowBuilder.Add(row.Elements.ToImmutableArray());
        }

        return new PreExecutionRowCheck(policyPredicate, normalizedPolicyCols, writtenArray, indexMap, rowBuilder.MoveToImmutable());
    }

    /// <summary>
    /// Verifies that this check covers all <paramref name="requiredPolicyColumns"/> and matches the final verified AST rows.
    /// Called independently by <see cref="SecurityCoverageVerifier"/>.
    /// </summary>
    internal bool Covers(
        IReadOnlySet<string>? requiredPolicyColumns,
        IReadOnlyList<SqlIdentifier> columns,
        ValuesQueryBody values)
    {
        if (requiredPolicyColumns is not null)
        {
            foreach (var col in requiredPolicyColumns)
            {
                if (!_policyColumns.Contains(col.ToLowerInvariant()) || !_columnIndices.ContainsKey(col))
                {
                    return false;
                }
            }
        }

        if (_writtenColumns.Length != columns.Count || _rows.Length != values.Rows.Count)
        {
            return false;
        }

        for (int i = 0; i < columns.Count; i++)
        {
            if (!string.Equals(_writtenColumns[i], columns[i].Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Evaluates the compiled SELECT RLS predicate against every candidate row using the current request's rebound policy values
    /// and client parameter values. Throws <see cref="DmlCheckOptionViolationException"/> before execution if any row does not
    /// strictly evaluate to <c>true</c>.
    /// </summary>
    public void Validate(
        IReadOnlyDictionary<string, PolicyValue> policyValues,
        IReadOnlyDictionary<string, object?> clientParameterValues,
        TargetSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(policyValues);
        ArgumentNullException.ThrowIfNull(clientParameterValues);

        var normalizedClient = NormalizeClientParameters(clientParameterValues);
        foreach (var row in _rows)
        {
            var rowValues = new Dictionary<string, object?>(_policyColumns.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var col in _policyColumns)
            {
                int idx = _columnIndices[col];
                rowValues[col] = EvaluateValue(row[idx], rowValues: null, policyValues, normalizedClient);
            }

            bool? outcome = EvaluatePredicate(_policyPredicate, rowValues, policyValues, normalizedClient);
            if (outcome != true)
            {
                throw new DmlCheckOptionViolationException(dialect);
            }
        }
    }

    /// <summary>
    /// Validates a single typed row map (used by bounded row write / MERGE upsert) directly against a table's SELECT RLS predicate.
    /// Throws <see cref="SqlCompileNotSupportedException"/> if the predicate uses unsupported constructs,
    /// <see cref="SecurityException"/> if a policy column is missing from <paramref name="rowValues"/>,
    /// or <see cref="DmlCheckOptionViolationException"/> if the row does not satisfy the predicate.
    /// </summary>
    public static void ValidateRowMap(
        Expression policyPredicate,
        IReadOnlySet<string> policyColumns,
        IReadOnlyDictionary<string, PolicyValue> policyValues,
        IReadOnlyDictionary<string, object?> rowValues,
        TargetSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(policyPredicate);
        ArgumentNullException.ThrowIfNull(policyColumns);
        ArgumentNullException.ThrowIfNull(policyValues);
        ArgumentNullException.ThrowIfNull(rowValues);

        var indexMap = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (var key in rowValues.Keys)
        {
            indexMap[key] = i++;
        }

        var map = indexMap.ToImmutable();
        foreach (var col in policyColumns)
        {
            if (!map.ContainsKey(col))
            {
                throw new SecurityException("A bounded write into a table with a row-level policy must supply every column the policy references.");
            }
        }

        ValidatePolicyAst(policyPredicate, map);

        var emptyClient = ImmutableDictionary<string, object?>.Empty;
        bool? outcome = EvaluatePredicate(policyPredicate, rowValues, policyValues, emptyClient);
        if (outcome != true)
        {
            throw new DmlCheckOptionViolationException(dialect);
        }
    }

    private static void ValidatePolicyAst(Expression expr, ImmutableDictionary<string, int> columnIndices)
    {
        ExecutionStackGuard.Ensure();
        switch (expr)
        {
            case SecurityPredicateExpression sp:
                ValidatePolicyAst(sp.Predicate, columnIndices);
                return;
            case ParenthesizedExpression p:
                ValidatePolicyAst(p.Expression, columnIndices);
                return;
            case ColumnReference col:
                var name = col.Name.SimpleName;
                if (!columnIndices.ContainsKey(name))
                {
                    throw new SecurityException("An INSERT into a table with a row-level policy must supply every column the policy references.");
                }
                return;
            case LiteralExpression:
            case PolicyParameterExpression:
            case ParameterReference:
                return;
            case CastExpression cast:
                ValidatePolicyAst(cast.Operand, columnIndices);
                return;
            case UnaryExpression { Operator: UnaryOperator.Not or UnaryOperator.Negate or UnaryOperator.IsNull or UnaryOperator.IsNotNull } unary:
                ValidatePolicyAst(unary.Operand, columnIndices);
                return;
            case BinaryExpression
            {
                Operator: BinaryOperator.And or BinaryOperator.Or
                    or BinaryOperator.Equal or BinaryOperator.NotEqual
                    or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
                    or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual
            } bin:
                ValidatePolicyAst(bin.Left, columnIndices);
                ValidatePolicyAst(bin.Right, columnIndices);
                return;
            case InListExpression inList:
                ValidatePolicyAst(inList.Operand, columnIndices);
                foreach (var item in inList.Items)
                {
                    ValidatePolicyAst(item, columnIndices);
                }
                return;
            case BetweenExpression between:
                ValidatePolicyAst(between.Operand, columnIndices);
                ValidatePolicyAst(between.Lower, columnIndices);
                ValidatePolicyAst(between.Upper, columnIndices);
                return;
            case IsDistinctFromExpression distinct:
                ValidatePolicyAst(distinct.Left, columnIndices);
                ValidatePolicyAst(distinct.Right, columnIndices);
                return;
            default:
                throw new SqlCompileNotSupportedException(
                    SqlCompileNotSupportedReason.Construct,
                    $"pre-execution row-level security evaluation does not support '{expr.GetType().Name}'");
        }
    }

    private static void ValidateCellAst(Expression expr)
    {
        ExecutionStackGuard.Ensure();
        switch (expr)
        {
            case LiteralExpression:
            case ParameterReference:
            case PolicyParameterExpression:
                return;
            case ParenthesizedExpression p:
                ValidateCellAst(p.Expression);
                return;
            case CastExpression cast:
                ValidateCellAst(cast.Operand);
                return;
            case UnaryExpression { Operator: UnaryOperator.Negate } unary:
                ValidateCellAst(unary.Operand);
                return;
            default:
                throw new SqlCompileNotSupportedException(
                    SqlCompileNotSupportedReason.Construct,
                    $"pre-execution row-level security evaluation requires literal or parameter values for policy columns, not '{expr.GetType().Name}'");
        }
    }

    private static IReadOnlyDictionary<string, object?> NormalizeClientParameters(IReadOnlyDictionary<string, object?> clientValues)
    {
        if (clientValues.Count == 0)
        {
            return ImmutableDictionary<string, object?>.Empty;
        }

        var map = new Dictionary<string, object?>(clientValues.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in clientValues)
        {
            if (string.IsNullOrEmpty(key)) continue;
            var trimmed = key.TrimStart('@', ':', '$');
            map[trimmed] = value is DBNull ? null : value;
        }

        return map;
    }

    private static bool? EvaluatePredicate(
        Expression expr,
        IReadOnlyDictionary<string, object?> rowValues,
        IReadOnlyDictionary<string, PolicyValue> policyValues,
        IReadOnlyDictionary<string, object?> clientValues)
    {
        ExecutionStackGuard.Ensure();
        switch (expr)
        {
            case SecurityPredicateExpression sp:
                return EvaluatePredicate(sp.Predicate, rowValues, policyValues, clientValues);
            case ParenthesizedExpression p:
                return EvaluatePredicate(p.Expression, rowValues, policyValues, clientValues);
            case LiteralExpression { Type: LiteralType.Boolean, Value: bool b }:
                return b;
            case LiteralExpression { Type: LiteralType.Null }:
                return null;
            case UnaryExpression { Operator: UnaryOperator.Not } notExpr:
                return ThreeValuedNot(EvaluatePredicate(notExpr.Operand, rowValues, policyValues, clientValues));
            case UnaryExpression { Operator: UnaryOperator.IsNull } isNullExpr:
                return EvaluateValue(isNullExpr.Operand, rowValues, policyValues, clientValues) is null;
            case UnaryExpression { Operator: UnaryOperator.IsNotNull } isNotNullExpr:
                return EvaluateValue(isNotNullExpr.Operand, rowValues, policyValues, clientValues) is not null;
            case BinaryExpression { Operator: BinaryOperator.And } andExpr:
            {
                var left = EvaluatePredicate(andExpr.Left, rowValues, policyValues, clientValues);
                if (left == false) return false;
                var right = EvaluatePredicate(andExpr.Right, rowValues, policyValues, clientValues);
                if (right == false) return false;
                return left == true && right == true ? true : null;
            }
            case BinaryExpression { Operator: BinaryOperator.Or } orExpr:
            {
                var left = EvaluatePredicate(orExpr.Left, rowValues, policyValues, clientValues);
                if (left == true) return true;
                var right = EvaluatePredicate(orExpr.Right, rowValues, policyValues, clientValues);
                if (right == true) return true;
                return left == false && right == false ? false : null;
            }
            case BinaryExpression
            {
                Operator: BinaryOperator.Equal or BinaryOperator.NotEqual
                    or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
                    or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual
            } cmp:
            {
                var leftVal = EvaluateValue(cmp.Left, rowValues, policyValues, clientValues);
                var rightVal = EvaluateValue(cmp.Right, rowValues, policyValues, clientValues);
                return CompareValues(leftVal, rightVal, cmp.Operator);
            }
            case InListExpression inList:
            {
                var lhs = EvaluateValue(inList.Operand, rowValues, policyValues, clientValues);
                if (lhs is null) return null;
                bool sawNull = false;
                foreach (var item in inList.Items)
                {
                    var rhs = EvaluateValue(item, rowValues, policyValues, clientValues);
                    if (rhs is null)
                    {
                        sawNull = true;
                        continue;
                    }

                    if (CompareValues(lhs, rhs, BinaryOperator.Equal) == true)
                    {
                        return inList.IsNotIn ? false : true;
                    }
                }

                bool? membership = sawNull ? null : false;
                return inList.IsNotIn ? ThreeValuedNot(membership) : membership;
            }
            case BetweenExpression between:
            {
                var val = EvaluateValue(between.Operand, rowValues, policyValues, clientValues);
                var lo = EvaluateValue(between.Lower, rowValues, policyValues, clientValues);
                var hi = EvaluateValue(between.Upper, rowValues, policyValues, clientValues);
                var ge = CompareValues(val, lo, BinaryOperator.GreaterThanOrEqual);
                var le = CompareValues(val, hi, BinaryOperator.LessThanOrEqual);
                bool? inRange = ge == false || le == false ? false : (ge == true && le == true ? true : null);
                return between.IsNotBetween ? ThreeValuedNot(inRange) : inRange;
            }
            case IsDistinctFromExpression distinct:
            {
                var leftVal = EvaluateValue(distinct.Left, rowValues, policyValues, clientValues);
                var rightVal = EvaluateValue(distinct.Right, rowValues, policyValues, clientValues);
                bool equal = leftVal is null && rightVal is null
                    || (leftVal is not null && rightVal is not null && CompareValues(leftVal, rightVal, BinaryOperator.Equal) == true);
                return distinct.IsNotDistinctFrom ? equal : !equal;
            }
            default:
                return false;
        }
    }

    private static object? EvaluateValue(
        Expression expr,
        IReadOnlyDictionary<string, object?>? rowValues,
        IReadOnlyDictionary<string, PolicyValue> policyValues,
        IReadOnlyDictionary<string, object?> clientValues)
    {
        ExecutionStackGuard.Ensure();
        switch (expr)
        {
            case ParenthesizedExpression p:
                return EvaluateValue(p.Expression, rowValues, policyValues, clientValues);
            case LiteralExpression lit:
                return lit.Type == LiteralType.Null || lit.Value is DBNull ? null : lit.Value;
            case ColumnReference col:
                if (rowValues is not null && rowValues.TryGetValue(col.Name.SimpleName, out var colVal))
                {
                    return colVal is DBNull ? null : colVal;
                }
                throw new SecurityException("A policy column value could not be resolved for pre-execution row verification.");
            case PolicyParameterExpression pol:
                if (policyValues.TryGetValue(pol.Name, out var polVal))
                {
                    return polVal.Value is DBNull ? null : polVal.Value;
                }
                throw new SecurityException("A policy parameter value could not be resolved for pre-execution row verification.");
            case ParameterReference pr:
            {
                var normalized = pr.Name.TrimStart('@', ':', '$');
                if (clientValues.TryGetValue(normalized, out var val))
                {
                    return val is DBNull ? null : val;
                }
                throw new SecurityException("A client parameter value could not be resolved for pre-execution row verification.");
            }
            case UnaryExpression { Operator: UnaryOperator.Negate } neg:
            {
                var inner = EvaluateValue(neg.Operand, rowValues, policyValues, clientValues);
                return inner switch
                {
                    null => null,
                    int i => -(long)i,
                    long l => -l,
                    decimal d => -d,
                    double dbl => -dbl,
                    float f => -(double)f,
                    _ => throw new SecurityException("Unsupported negated value in pre-execution row verification.")
                };
            }
            case CastExpression cast:
            {
                var inner = EvaluateValue(cast.Operand, rowValues, policyValues, clientValues);
                return CoerceForCast(inner, cast.TargetType);
            }
            default:
                throw new SecurityException("Unsupported expression in pre-execution row verification.");
        }
    }

    private static object? CoerceForCast(object? value, string targetType)
    {
        if (value is null) return null;
        var norm = targetType.Trim().ToLowerInvariant();
        var paren = norm.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0) norm = norm[..paren];

        return norm switch
        {
            "varchar" or "nvarchar" or "char" or "nchar" or "text" or "string" =>
                Convert.ToString(value, CultureInfo.InvariantCulture),
            "int" or "integer" or "int4" or "bigint" or "int8" or "smallint" or "int2" =>
                Convert.ToInt64(value, CultureInfo.InvariantCulture),
            "decimal" or "numeric" or "double" or "float" or "real" =>
                Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            "bool" or "boolean" or "bit" =>
                Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            "uuid" or "uniqueidentifier" =>
                value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
            _ => value
        };
    }

    private static bool? ThreeValuedNot(bool? value) => value.HasValue ? !value.Value : null;

    private static bool? CompareValues(object? left, object? right, BinaryOperator op)
    {
        if (left is null || right is null)
        {
            return null;
        }

        int cmp;
        if (TryToNumeric(left, out var lNum) && TryToNumeric(right, out var rNum))
        {
            cmp = lNum.CompareTo(rNum);
        }
        else if (left is string ls && right is string rs)
        {
            // Byte-exact ordinal comparison matches InsertCheckPolicy and TenantPredicateFactory.
            cmp = string.CompareOrdinal(ls, rs);
        }
        else if (left is bool lb && right is bool rb)
        {
            cmp = lb.CompareTo(rb);
        }
        else if (TryToGuid(left, out var lg) && TryToGuid(right, out var rg))
        {
            cmp = lg.CompareTo(rg);
        }
        else if (TryToDateTimeOffset(left, out var ld) && TryToDateTimeOffset(right, out var rd))
        {
            cmp = ld.UtcDateTime.CompareTo(rd.UtcDateTime);
        }
        else
        {
            // Fail closed on incompatible runtime types.
            return false;
        }

        return op switch
        {
            BinaryOperator.Equal => cmp == 0,
            BinaryOperator.NotEqual => cmp != 0,
            BinaryOperator.LessThan => cmp < 0,
            BinaryOperator.LessThanOrEqual => cmp <= 0,
            BinaryOperator.GreaterThan => cmp > 0,
            BinaryOperator.GreaterThanOrEqual => cmp >= 0,
            _ => false
        };
    }

    private static bool TryToNumeric(object value, out decimal result)
    {
        switch (value)
        {
            case sbyte v: result = v; return true;
            case byte v: result = v; return true;
            case short v: result = v; return true;
            case ushort v: result = v; return true;
            case int v: result = v; return true;
            case uint v: result = v; return true;
            case long v: result = v; return true;
            case ulong v: result = v; return true;
            case decimal v: result = v; return true;
            case float v when !float.IsNaN(v) && !float.IsInfinity(v): result = (decimal)v; return true;
            case double v when !double.IsNaN(v) && !double.IsInfinity(v): result = (decimal)v; return true;
            default: result = 0m; return false;
        }
    }

    private static bool TryToGuid(object value, out Guid result)
    {
        if (value is Guid g)
        {
            result = g;
            return true;
        }

        if (value is string s && Guid.TryParse(s, out var parsed))
        {
            result = parsed;
            return true;
        }

        result = Guid.Empty;
        return false;
    }

    private static bool TryToDateTimeOffset(object value, out DateTimeOffset result)
    {
        if (value is DateTimeOffset dto)
        {
            result = dto;
            return true;
        }

        if (value is DateTime dt)
        {
            result = dt.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : new DateTimeOffset(dt);
            return true;
        }

        if (value is DateOnly d)
        {
            result = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            return true;
        }

        result = default;
        return false;
    }
}
