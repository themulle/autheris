namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// Second stage of <see cref="RowPolicyErrorGuard"/>: refuses implicit type coercions, which fail on row data. Every operand of a
/// comparison, BETWEEN, IN list, CASE branch set, coalesce, nullif, greatest and least must resolve to one value family (text, numeric,
/// temporal, boolean); an operand whose family is unknown is refused. Date part extractors need a temporal source. Column families come
/// from the catalog, and from the projections of CTEs and derived tables of the statement. Details: private advisory.
/// </summary>
internal sealed class RowPolicyCoercionGuard(ITableCatalog catalog, bool evaluationFenced = false) : ColumnScopeTracker(catalog)
{
    private enum Fam { Unknown, Any, Text, StringLiteral, Bind, Numeric, Temporal, Boolean }

    private int _wideningAggregateCastDepth;

    // The root ORDER BY that sees the scope of its single query block, and, while it is visited, the families of that block's output names.
    private OrderByClause? _scopedOrderBy;
    private QuerySpecification? _scopedBody;
    private int _selectDepth;
    private Dictionary<string, Fam>? _orderByAliases;

    private readonly Dictionary<string, Dictionary<string, Fam>> _derived = new(StringComparer.Ordinal);
    private readonly Dictionary<QuerySpecification, List<(string? Name, Fam Family)>> _outputs = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<string, BindFamily> _requirements = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The family each client bind (by client parameter or list name) must conform to, as found while visiting the statement.</summary>
    public IReadOnlyDictionary<string, BindFamily> Requirements => _requirements;

    private static BindFamily? RequiredFamilyOf(Fam family) => family switch
    {
        Fam.Numeric => BindFamily.Numeric,
        Fam.Temporal => BindFamily.Temporal,
        Fam.Boolean => BindFamily.Boolean,
        Fam.Text or Fam.StringLiteral => BindFamily.Text,
        _ => null
    };

    /// <summary>Records that a bind operand meets <paramref name="other"/>; a bind that needs two families, or an unnamed bind marker that needs one, is refused.</summary>
    private void Record(Expression operand, Fam other)
    {
        if (RequiredFamilyOf(other) is not { } required) return;
        switch (Unwrap(operand))
        {
            case ParameterReference parameter:
                if (_requirements.TryGetValue(parameter.Name, out var existing) && existing != required) throw Refuse();
                _requirements[parameter.Name] = required;
                break;
            case NamedBindMarkerExpression:
                throw Refuse();
        }
    }

    /// <summary>Records, for every bind operand, the family of the other operands.</summary>
    private void Bind(IReadOnlyList<Expression> operands)
    {
        var others = new List<Fam>();
        foreach (var operand in operands)
        {
            if (Unwrap(operand) is not (ParameterReference or NamedBindMarkerExpression)) others.Add(Of(operand));
        }

        var family = Combine(others);
        bool hasBind = operands.Any(o => Unwrap(o) is ParameterReference or NamedBindMarkerExpression);

        // Operands that are all binds or untyped constants leave nothing to take the family from: the engine would coerce one value to the other.
        if (hasBind && family is Fam.Any or Fam.Bind) throw Refuse();

        foreach (var operand in operands) Record(operand, family);
    }

    private static RowPolicyExpressionRejectedException Refuse() => new();

    private static readonly string[] IsoFormats = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFF"];

    private static Expression Unwrap(Expression expression)
    {
        while (expression is ParenthesizedExpression p) expression = p.Expression;
        return expression;
    }

    private static string Fold(SqlIdentifier id) => id.Value.ToLowerInvariant();

    private static Fam OfType(string? dataType) => AggregateTypeClasses.Classify(dataType) switch
    {
        AggregateTypeClass.Numeric => Fam.Numeric,
        AggregateTypeClass.Text => Fam.Text,
        AggregateTypeClass.Temporal => Fam.Temporal,
        AggregateTypeClass.Boolean => Fam.Boolean,
        _ => Fam.Unknown
    };

    private static bool Compatible(Fam a, Fam b)
    {
        if (a == Fam.Unknown || b == Fam.Unknown) return false;
        if (a == Fam.Any || b == Fam.Any || a == Fam.Bind || b == Fam.Bind || a == b) return true;
        // A client bind carries a value that is only known at execution (the plan is cached): it is compatible with every family, and the
        // family it must conform to is recorded on its slot (see Requirements) so the binder checks the value before any command exists.
        return (a, b) is (Fam.StringLiteral, Fam.Text or Fam.Temporal) or (Fam.Text or Fam.Temporal, Fam.StringLiteral);
    }

    private static Fam Combine(IEnumerable<Fam> families)
    {
        var result = Fam.Any;
        foreach (var f in families)
        {
            if (!Compatible(result, f)) return Fam.Unknown;
            if (result is Fam.Any or Fam.StringLiteral or Fam.Bind && f != Fam.Any) result = f;
        }

        return result;
    }

    private Fam Of(Expression expression)
    {
        switch (expression)
        {
            case ParenthesizedExpression p: return Of(p.Expression);
            case ColumnReference column: return OfColumn(column);
            case LiteralExpression literal:
                return literal.Type switch
                {
                    LiteralType.Null => Fam.Any,
                    LiteralType.Boolean => Fam.Boolean,
                    LiteralType.Integer or LiteralType.Decimal => Fam.Numeric,
                    LiteralType.String => Fam.StringLiteral,
                    _ => Fam.Unknown
                };
            case TypedLiteralExpression or CurrentDateTimeExpression or DateFunctionExpression: return Fam.Temporal;
            case ParameterReference or NamedBindMarkerExpression: return Fam.Bind;
            case IntervalLiteralExpression or PolicyParameterExpression or PageOffsetParameterExpression: return Fam.Any;
            case ScalarSubqueryExpression scalar: return FirstFamily(scalar.Subquery);
            case BinaryExpression b:
                return b.Operator switch
                {
                    BinaryOperator.Add or BinaryOperator.Subtract when b.Left is IntervalLiteralExpression || b.Right is IntervalLiteralExpression => Fam.Temporal,
                    BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo => Fam.Numeric,
                    BinaryOperator.Concat => Fam.Text,
                    _ => Fam.Boolean
                };
            case UnaryExpression u: return u.Operator == UnaryOperator.Negate ? Fam.Numeric : Fam.Boolean;
            case LikeExpression or InListExpression or InSubqueryExpression or ExistsExpression or IsDistinctFromExpression or BetweenExpression
                or QuantifiedComparisonExpression: return Fam.Boolean;
            case ExtractExpression or PositionExpression: return Fam.Numeric;
            case SubstringExpression or TrimExpression: return Fam.Text;
            case CaseExpression c:
                return Combine(c.WhenClauses.Select(w => Of(w.Result)).Concat(c.ElseResult is null ? [] : [Of(c.ElseResult)]));
            case CastExpression cast: return OfType(cast.TargetType);
            case FunctionCallExpression call: return OfFunction(call);
            default: return Fam.Unknown;
        }
    }

    private Fam OfFunction(FunctionCallExpression call)
    {
        if (call.Name.Parts.Count != 1) return Fam.Unknown;
        string name = call.Name.Parts[0].Value.ToLowerInvariant();
        return name switch
        {
            "coalesce" or "nullif" or "greatest" or "least" or "min" or "max" or "measure" => Combine(call.Arguments.Select(Of)),
            "lower" or "upper" or "trim" or "ltrim" or "rtrim" or "concat" or "substring" or "substr" => Fam.Text,
            "length" or "char_length" or "character_length" or "count" or "sum" or "avg" or "year" or "month" or "day" or "hour" or "minute" or "second"
                or "quarter" or "week" or "day_of_week" or "day_of_month" or "day_of_year" => Fam.Numeric,
            "convert_timezone" => Fam.Temporal,
            _ when evaluationFenced => name switch
            {
                "nvl" or "isnull" or "ifnull" => Combine(call.Arguments.Select(Of)),
                "replace" or "to_char" => Fam.Text,
                "row_number" or "rank" or "dense_rank" or "ntile" or "grouping"
                    or "abs" or "round" or "ceil" or "ceiling" or "floor" or "sqrt" or "ln" or "log" or "log10"
                    or "exp" or "power" or "mod" or "sign" or "trunc" or "strpos" or "instr" or "charindex"
                    or "len" or "date_diff" => Fam.Numeric,
                "date_add" or "date_trunc" or "to_date" or "to_timestamp" or "date_parse" => Fam.Temporal,
                _ => Fam.Unknown
            },
            _ => Fam.Unknown
        };
    }

    private Fam OfColumn(ColumnReference column)
    {
        // The root ORDER BY of a single query block: an unqualified name that is an output name is that projection (repeated names are unknown).
        if (_orderByAliases is not null && column.Name.Parts.Count == 1 && _orderByAliases.TryGetValue(Fold(column.Name.Parts[0]), out var aliased)) return aliased;

        if ((ResolveColumn(column) ?? ResolveOuterColumn(column)) is { } resolved) return OfType(resolved.DataType);

        var parts = column.Name.Parts;
        string name = Fold(parts[^1]);
        if (parts.Count >= 2)
        {
            return _derived.TryGetValue(Fold(parts[^2]), out var map) && map.TryGetValue(name, out var fam) ? fam : Fam.Unknown;
        }

        var found = new List<Fam>();
        foreach (var map in _derived.Values)
        {
            if (map.TryGetValue(name, out var fam)) found.Add(fam);
        }

        return found.Count == 0 ? Fam.Unknown : Combine(found);
    }

    private void Require(params Fam[] families)
    {
        if (!Combine(families).Equals(Fam.Unknown) && families.All(f => f != Fam.Unknown)) return;
        throw Refuse();
    }

    // ---- derived column families ----

    /// <summary>Output columns of a query block by position: the name the column is visible under (null when it has none) and its family.</summary>
    private List<(string? Name, Fam Family)> OutputsOf(QuerySpecification spec)
    {
        var list = new List<(string?, Fam)>(spec.Projections.Count);
        foreach (var item in spec.Projections)
        {
            if (item is not ColumnSelectItem column)
            {
                list.Add((null, Fam.Unknown));
                continue;
            }

            string? name = column.Alias is not null ? Fold(column.Alias) : column.Expression is ColumnReference cr ? Fold(cr.Name.Parts[^1]) : null;
            list.Add((name, Of(column.Expression)));
        }

        return list;
    }

    private Dictionary<string, Fam> Named(QuerySpecification spec, IReadOnlyList<SqlIdentifier>? aliases)
    {
        var map = new Dictionary<string, Fam>(StringComparer.Ordinal);
        var outputs = _outputs[spec];
        for (int i = 0; i < outputs.Count; i++)
        {
            string? name = aliases is not null ? (i < aliases.Count ? Fold(aliases[i]) : null) : outputs[i].Name;
            if (name is not null) map[name] = outputs[i].Family;
        }

        return map;
    }

    /// <summary>The family of the single output column of a subquery; unknown when it has none or several.</summary>
    private Fam FirstFamily(SelectStatement subquery) =>
        LeftMost(subquery.Body) is { } spec && _outputs.TryGetValue(spec, out var outputs) && outputs.Count == 1 ? outputs[0].Family : Fam.Unknown;

    private static QuerySpecification? LeftMost(QueryBody body) => body switch
    {
        QuerySpecification q => q,
        SetOperationQuery s => LeftMost(s.Left),
        _ => null
    };

    public override SqlNode VisitQuerySpecification(QuerySpecification node)
    {
        RegisterDerivedSources(node.From);
        var result = (QuerySpecification)base.VisitQuerySpecification(node);
        if (PushLastBlock())
        {
            try
            {
                _outputs[node] = OutputsOf(result);
            }
            finally
            {
                PopBlock();
            }
        }

        return result;
    }

    public override SqlNode VisitSelectStatement(SelectStatement node)
    {
        // Only the root statement and only a single query block have a scope to resolve in; a set operation body, a CTE or a subquery keep the
        // unresolved (refused) families.
        bool root = _selectDepth++ == 0;
        var (previousOrderBy, previousBody, previousAliases) = (_scopedOrderBy, _scopedBody, _orderByAliases);
        _scopedBody = root ? node.Body as QuerySpecification : null;
        _scopedOrderBy = _scopedBody is null ? null : node.OrderBy;
        _orderByAliases = null;
        try
        {
            return base.VisitSelectStatement(node);
        }
        finally
        {
            _selectDepth--;
            (_scopedOrderBy, _scopedBody, _orderByAliases) = (previousOrderBy, previousBody, previousAliases);
        }
    }

    public override SqlNode VisitOrderByClause(OrderByClause node)
    {
        if (!ReferenceEquals(node, _scopedOrderBy) || _scopedBody is not { } spec || !_outputs.TryGetValue(spec, out var outputs) || !PushLastBlock())
        {
            return base.VisitOrderByClause(node);
        }

        var previous = _orderByAliases;
        var aliases = new Dictionary<string, Fam>(StringComparer.Ordinal);
        foreach (var (name, family) in outputs)
        {
            if (name is null) continue;
            aliases[name] = aliases.ContainsKey(name) ? Fam.Unknown : family;
        }

        _orderByAliases = aliases;
        try
        {
            return base.VisitOrderByClause(node);
        }
        finally
        {
            _orderByAliases = previous;
            PopBlock();
        }
    }

    private void RegisterDerivedSources(TableSource? source)
    {
        switch (source)
        {
            case SubqueryTableSource sub:
                if (LeftMost(sub.Subquery.Body) is { } spec)
                {
                    if (!_outputs.ContainsKey(spec)) Visit(spec);
                    _derived[Fold(sub.Alias)] = Named(spec, sub.ColumnAliases);
                }

                break;
            case JoinedTableSource joined:
                RegisterDerivedSources(joined.Left);
                RegisterDerivedSources(joined.Right);
                break;
        }
    }

    public override SqlNode VisitCommonTableExpression(CommonTableExpression node)
    {
        // The anchor (leftmost member) fixes the column families, also for the recursive member that reads the CTE itself.
        if (LeftMost(node.Query.Body) is { } anchor)
        {
            if (!_outputs.ContainsKey(anchor)) Visit(anchor);
            _derived[Fold(node.Name)] = Named(anchor, node.ColumnAliases);
        }

        return base.VisitCommonTableExpression(node);
    }

    // ---- coercion checks ----

    private static bool IsComparison(BinaryOperator op) =>
        op is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual;

    public override SqlNode VisitBinaryExpression(BinaryExpression node)
    {
        var result = (BinaryExpression)base.VisitBinaryExpression(node);
        if (IsComparison(result.Operator))
        {
            Require(Of(result.Left), Of(result.Right));
            Bind([result.Left, result.Right]);
        }
        else if (result.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            Record(result.Left, Fam.Boolean);
            Record(result.Right, Fam.Boolean);
        }
        else if (result.Operator is BinaryOperator.Divide or BinaryOperator.Modulo)
        {
            Require(Fam.Numeric, Of(result.Left));
            Record(result.Left, Fam.Numeric);
        }
        return result;
    }

    public override SqlNode VisitInSubqueryExpression(InSubqueryExpression node)
    {
        var result = (InSubqueryExpression)base.VisitInSubqueryExpression(node);
        Require(Of(result.Operand), FirstFamily(result.Subquery));
        Record(result.Operand, FirstFamily(result.Subquery));
        return result;
    }

    public override SqlNode VisitQuantifiedComparisonExpression(QuantifiedComparisonExpression node)
    {
        var result = (QuantifiedComparisonExpression)base.VisitQuantifiedComparisonExpression(node);
        Require(Of(result.Left), FirstFamily(result.Subquery));
        Record(result.Left, FirstFamily(result.Subquery));
        return result;
    }

    public override SqlNode VisitSetOperationQuery(SetOperationQuery node)
    {
        var result = (SetOperationQuery)base.VisitSetOperationQuery(node);
        if (LeftMost(result.Left) is { } left && LeftMost(result.Right) is { } right
            && _outputs.TryGetValue(left, out var l) && _outputs.TryGetValue(right, out var r) && l.Count == r.Count)
        {
            for (int i = 0; i < l.Count; i++) Require(l[i].Family, r[i].Family);
        }

        return result;
    }

    public override SqlNode VisitUnaryExpression(UnaryExpression node)
    {
        var result = (UnaryExpression)base.VisitUnaryExpression(node);
        if (result.Operator == UnaryOperator.Not) Record(result.Operand, Fam.Boolean);
        return result;
    }

    public override SqlNode VisitIsDistinctFromExpression(IsDistinctFromExpression node)
    {
        var result = (IsDistinctFromExpression)base.VisitIsDistinctFromExpression(node);
        Require(Of(result.Left), Of(result.Right));
        Bind([result.Left, result.Right]);
        return result;
    }

    public override SqlNode VisitBetweenExpression(BetweenExpression node)
    {
        var result = (BetweenExpression)base.VisitBetweenExpression(node);
        Require(Of(result.Operand), Of(result.Lower), Of(result.Upper));
        Bind([result.Operand, result.Lower, result.Upper]);
        return result;
    }

    public override SqlNode VisitInListExpression(InListExpression node)
    {
        var result = (InListExpression)base.VisitInListExpression(node);
        Require([Of(result.Operand), .. result.Items.Select(Of)]);
        Bind([result.Operand, .. result.Items]);
        return result;
    }

    public override SqlNode VisitCaseExpression(CaseExpression node)
    {
        var result = (CaseExpression)base.VisitCaseExpression(node);
        Require(result.WhenClauses.Select(w => Of(w.Result)).Concat(result.ElseResult is null ? [] : [Of(result.ElseResult)]).ToArray());
        Bind([.. result.WhenClauses.Select(w => w.Result), .. (result.ElseResult is null ? [] : new[] { result.ElseResult })]);
        if (result.Operand is not null)
        {
            Require([Of(result.Operand), .. result.WhenClauses.Select(w => Of(w.Condition))]);
            Bind([result.Operand, .. result.WhenClauses.Select(w => w.Condition)]);
        }
        return result;
    }

    public override SqlNode VisitFunctionCallExpression(FunctionCallExpression node)
    {
        bool wideningAgg = RowPolicyErrorGuard.IsSafeAggregateForm(node)
            && node.Arguments.Count == 1
            && Unwrap(node.Arguments[0]) is CastExpression { IsTryCast: false };
        if (wideningAgg) _wideningAggregateCastDepth++;
        FunctionCallExpression result;
        try
        {
            result = (FunctionCallExpression)base.VisitFunctionCallExpression(node);
        }
        finally
        {
            if (wideningAgg) _wideningAggregateCastDepth--;
        }

        if (result.Name.Parts.Count != 1) return result;
        switch (result.Name.Parts[0].Value.ToLowerInvariant())
        {
            case "coalesce" or "nullif" or "greatest" or "least":
                Require(result.Arguments.Select(Of).ToArray());
                Bind(result.Arguments);
                break;
            case "year" or "month" or "day" or "hour" or "minute" or "second" or "quarter" or "week" or "day_of_week" or "day_of_month" or "day_of_year":
                foreach (var argument in result.Arguments)
                {
                    Require(Fam.Temporal, Of(argument));
                    Record(argument, Fam.Temporal);
                }
                break;
        }

        return result;
    }

    public override SqlNode VisitCastExpression(CastExpression node)
    {
        var result = (CastExpression)base.VisitCastExpression(node);
        if (result.IsTryCast) return result;

        var target = OfType(result.TargetType);
        var source = Of(result.Operand);

        // Non-raising gateway aggregate widening (CAST(<numeric column> AS decimal|double)) inside SUM/AVG cannot raise on numeric row values.
        if (_wideningAggregateCastDepth > 0 && RowPolicyErrorGuard.IsWideningAggregateCast(result) && source == Fam.Numeric)
        {
            return result;
        }

        // Under a proven optimizer evaluation fence, explicit CASTs over column expressions run strictly on visible rows.
        if (evaluationFenced && RowPolicyErrorGuard.HasColumn(result.Operand))
        {
            return result;
        }

        // A cast raises on a value that does not convert: only to text, between temporal values, of NULL, or numeric literals to numeric under a fence.
        if (target == Fam.Text || (target == Fam.Temporal && source == Fam.Temporal) || Unwrap(result.Operand) is LiteralExpression { Type: LiteralType.Null }) return result;

        if (evaluationFenced && target == Fam.Numeric && Unwrap(result.Operand) is LiteralExpression { Type: LiteralType.Integer or LiteralType.Decimal })
        {
            return result;
        }

        // A date or time literal that parses cannot fail, wherever the cast sits.
        if (target == Fam.Temporal && Unwrap(result.Operand) is LiteralExpression { Type: LiteralType.String, Value: string text }
            && DateTimeOffset.TryParseExact(text, IsoFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
        {
            return result;
        }

        throw Refuse();
    }

    public override SqlNode VisitExtractExpression(ExtractExpression node)
    {
        var result = (ExtractExpression)base.VisitExtractExpression(node);
        Require(Fam.Temporal, Of(result.Source));
        Record(result.Source, Fam.Temporal);
        return result;
    }

    public override SqlNode VisitDateFunction(DateFunctionExpression node)
    {
        var result = (DateFunctionExpression)base.VisitDateFunction(node);
        Require(Fam.Temporal, Of(result.Source));
        Record(result.Source, Fam.Temporal);
        return result;
    }
}
