namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// Second stage of <see cref="RowPolicyErrorGuard"/>: refuses implicit type coercions, which fail on row data. Every operand of a
/// comparison, BETWEEN, IN list, CASE branch set, coalesce, nullif, greatest and least must resolve to one value family (text, numeric,
/// temporal, boolean); an operand whose family is unknown is refused. Date part extractors need a temporal source. Column families come
/// from the catalog, and from the projections of CTEs and derived tables of the statement. Details: private advisory.
/// </summary>
internal sealed class RowPolicyCoercionGuard(ITableCatalog catalog) : ColumnScopeTracker(catalog)
{
    private enum Fam { Unknown, Any, Text, StringLiteral, Numeric, Temporal, Boolean }

    private readonly Dictionary<string, Dictionary<string, Fam>> _derived = new(StringComparer.Ordinal);
    private readonly Dictionary<QuerySpecification, List<(string? Name, Fam Family)>> _outputs = new(ReferenceEqualityComparer.Instance);

    private static SecurityException Refuse() => new(RowPolicyErrorGuard.Message);

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
        if (a == Fam.Any || b == Fam.Any || a == b) return true;
        return (a, b) is (Fam.StringLiteral, Fam.Text or Fam.Temporal) or (Fam.Text or Fam.Temporal, Fam.StringLiteral);
    }

    private static Fam Combine(IEnumerable<Fam> families)
    {
        var result = Fam.Any;
        foreach (var f in families)
        {
            if (!Compatible(result, f)) return Fam.Unknown;
            if (result is Fam.Any or Fam.StringLiteral && f != Fam.Any) result = f;
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
            case IntervalLiteralExpression or ParameterReference or NamedBindMarkerExpression or PolicyParameterExpression
                or PageOffsetParameterExpression: return Fam.Any;
            case ScalarSubqueryExpression scalar: return FirstFamily(scalar.Subquery);
            case BinaryExpression b:
                return b.Operator switch
                {
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
            "coalesce" or "nullif" or "greatest" or "least" or "min" or "max" => Combine(call.Arguments.Select(Of)),
            "lower" or "upper" or "trim" or "ltrim" or "rtrim" or "concat" or "substring" or "substr" => Fam.Text,
            "length" or "char_length" or "character_length" or "count" or "year" or "month" or "day" or "hour" or "minute" or "second"
                or "quarter" or "week" or "day_of_week" or "day_of_month" or "day_of_year" => Fam.Numeric,
            "convert_timezone" => Fam.Temporal,
            _ => Fam.Unknown
        };
    }

    private Fam OfColumn(ColumnReference column)
    {
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
        if (IsComparison(result.Operator)) Require(Of(result.Left), Of(result.Right));
        else if (result.Operator is BinaryOperator.Divide or BinaryOperator.Modulo) Require(Fam.Numeric, Of(result.Left));
        return result;
    }

    public override SqlNode VisitInSubqueryExpression(InSubqueryExpression node)
    {
        var result = (InSubqueryExpression)base.VisitInSubqueryExpression(node);
        Require(Of(result.Operand), FirstFamily(result.Subquery));
        return result;
    }

    public override SqlNode VisitQuantifiedComparisonExpression(QuantifiedComparisonExpression node)
    {
        var result = (QuantifiedComparisonExpression)base.VisitQuantifiedComparisonExpression(node);
        Require(Of(result.Left), FirstFamily(result.Subquery));
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

    public override SqlNode VisitIsDistinctFromExpression(IsDistinctFromExpression node)
    {
        var result = (IsDistinctFromExpression)base.VisitIsDistinctFromExpression(node);
        Require(Of(result.Left), Of(result.Right));
        return result;
    }

    public override SqlNode VisitBetweenExpression(BetweenExpression node)
    {
        var result = (BetweenExpression)base.VisitBetweenExpression(node);
        Require(Of(result.Operand), Of(result.Lower), Of(result.Upper));
        return result;
    }

    public override SqlNode VisitInListExpression(InListExpression node)
    {
        var result = (InListExpression)base.VisitInListExpression(node);
        Require([Of(result.Operand), .. result.Items.Select(Of)]);
        return result;
    }

    public override SqlNode VisitCaseExpression(CaseExpression node)
    {
        var result = (CaseExpression)base.VisitCaseExpression(node);
        Require(result.WhenClauses.Select(w => Of(w.Result)).Concat(result.ElseResult is null ? [] : [Of(result.ElseResult)]).ToArray());
        if (result.Operand is not null) Require([Of(result.Operand), .. result.WhenClauses.Select(w => Of(w.Condition))]);
        return result;
    }

    public override SqlNode VisitFunctionCallExpression(FunctionCallExpression node)
    {
        var result = (FunctionCallExpression)base.VisitFunctionCallExpression(node);
        if (result.Name.Parts.Count != 1) return result;
        switch (result.Name.Parts[0].Value.ToLowerInvariant())
        {
            case "coalesce" or "nullif" or "greatest" or "least":
                Require(result.Arguments.Select(Of).ToArray());
                break;
            case "year" or "month" or "day" or "hour" or "minute" or "second" or "quarter" or "week" or "day_of_week" or "day_of_month" or "day_of_year":
                foreach (var argument in result.Arguments) Require(Fam.Temporal, Of(argument));
                break;
        }

        return result;
    }

    public override SqlNode VisitCastExpression(CastExpression node)
    {
        var result = (CastExpression)base.VisitCastExpression(node);
        if (result.IsTryCast) return result;

        // A cast raises on a value that does not convert: only to text, between temporal values, or of NULL.
        var target = OfType(result.TargetType);
        var source = Of(result.Operand);
        if (target == Fam.Text || source == Fam.Any || (target == Fam.Temporal && source == Fam.Temporal)) return result;

        // A date or time literal that parses cannot fail, wherever the cast sits.
        if (target == Fam.Temporal && Unwrap(result.Operand) is LiteralExpression { Type: LiteralType.String, Value: string text }
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
        {
            return result;
        }

        throw Refuse();
    }

    public override SqlNode VisitExtractExpression(ExtractExpression node)
    {
        var result = (ExtractExpression)base.VisitExtractExpression(node);
        Require(Fam.Temporal, Of(result.Source));
        return result;
    }

    public override SqlNode VisitDateFunction(DateFunctionExpression node)
    {
        var result = (DateFunctionExpression)base.VisitDateFunction(node);
        Require(Fam.Temporal, Of(result.Source));
        return result;
    }
}
