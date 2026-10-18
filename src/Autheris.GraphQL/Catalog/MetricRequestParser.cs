using System.Globalization;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Language;

namespace Autheris.GraphQL.Catalog;

/// <summary>The validated arguments of one typed <c>queryMetrics</c> call.</summary>
internal sealed record ParsedMetricArguments(
    IReadOnlyList<string> Metrics,
    IReadOnlyList<string>? GroupBy,
    TreeFilter? Where,
    string? TimeGranularity,
    IReadOnlyList<TableOrderBy>? OrderBy,
    int? Limit);

/// <summary>
/// #112 A-8: the typed metric filter becomes a <see cref="TreeFilter"/> whose every leaf declares its value kind
/// (<see cref="TreeComparison.DeclaredKind"/>); the metric service checks it against the resolved column. Errors carry fixed messages and
/// stable codes and never echo a client value.
/// </summary>
internal static class MetricRequestParser
{
    private const int MaxNameLength = 128;

    public static ParsedMetricArguments Parse(
        IValueNode? metrics,
        IValueNode? groupBy,
        IValueNode? where,
        IValueNode? timeGranularity,
        IValueNode? orderBy,
        IValueNode? limit,
        AggregationOptions options,
        Func<string, IValueNode?> variable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(variable);

        var metricNames = Names(metrics, variable, options.MaxAggregates, required: true)!;
        var groupNames = Names(groupBy, variable, options.MaxGroupByColumns, required: false);
        if (groupNames != null && groupNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != groupNames.Count)
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        TreeFilter? filter = null;
        var resolvedWhere = Resolve(where, variable);
        if (!IsAbsent(resolvedWhere))
        {
            filter = ParseFilter(resolvedWhere!, variable, 0);
        }

        string? granularity = null;
        var resolvedGranularity = Resolve(timeGranularity, variable);
        if (!IsAbsent(resolvedGranularity))
        {
            granularity = resolvedGranularity is StringValueNode g && g.Value.Length is > 0 and <= MaxNameLength
                ? g.Value
                : throw AggregateErrors.ArgumentInvalid();
        }

        return new ParsedMetricArguments(
            metricNames,
            groupNames,
            filter,
            granularity,
            ParseOrderBy(orderBy, variable, options.MaxGroupByColumns + options.MaxAggregates),
            ParseLimit(limit, variable));
    }

    private static IValueNode? Resolve(IValueNode? node, Func<string, IValueNode?> variable) =>
        node is VariableNode v ? variable(v.Name.Value) : node;

    private static bool IsAbsent(IValueNode? node) => node is null or NullValueNode;

    private static IReadOnlyList<IValueNode> Items(IValueNode node, Func<string, IValueNode?> variable) => node switch
    {
        ListValueNode list => list.Items.Select(i => Resolve(i, variable) ?? NullValueNode.Default).ToList(),
        _ => [node]
    };

    private static List<string>? Names(IValueNode? node, Func<string, IValueNode?> variable, int max, bool required)
    {
        var resolved = Resolve(node, variable);
        if (IsAbsent(resolved))
        {
            return required ? throw AggregateErrors.ArgumentInvalid() : null;
        }

        var items = Items(resolved!, variable);
        if ((required && items.Count == 0) || items.Count > max)
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        var result = new List<string>(items.Count);
        foreach (var item in items)
        {
            if (item is not StringValueNode s || s.Value.Length is 0 or > MaxNameLength)
            {
                throw AggregateErrors.ArgumentInvalid();
            }

            result.Add(s.Value);
        }

        return result;
    }

    private static Dictionary<string, IValueNode?> Fields(ObjectValueNode obj, Func<string, IValueNode?> variable) =>
        obj.Fields.ToDictionary(f => f.Name.Value, f => Resolve(f.Value, variable), StringComparer.Ordinal);

    private static GraphQLException InvalidFilter() =>
        AggregateErrors.Create(AggregationErrorCodes.MetricFilterInvalid, MetricFilterInvalidException.FixedMessage);

    private static TreeFilter ParseFilter(IValueNode node, Func<string, IValueNode?> variable, int depth)
    {
        if (depth > TreeSqlCompiler.MaxFilterDepth)
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        if (node is not ObjectValueNode obj || obj.Fields.Count == 0)
        {
            throw InvalidFilter();
        }

        var parts = new List<TreeFilter>();
        foreach (var (name, value) in Fields(obj, variable))
        {
            if (IsAbsent(value))
            {
                throw InvalidFilter();
            }

            switch (name)
            {
                case "and":
                    parts.Add(new TreeAndFilter(Items(value!, variable).Select(i => ParseFilter(i, variable, depth + 1)).ToList()));
                    break;
                case "or":
                    parts.Add(new TreeOrFilter(Items(value!, variable).Select(i => ParseFilter(i, variable, depth + 1)).ToList()));
                    break;
                case "not":
                    parts.Add(new TreeNotFilter(ParseFilter(value!, variable, depth + 1)));
                    break;
                case "dimension":
                    parts.Add(ParseDimension(value!, variable, depth + 1));
                    break;
                default:
                    throw InvalidFilter();
            }
        }

        return parts.Count == 1 ? parts[0] : new TreeAndFilter(parts);
    }

    private static TreeFilter ParseDimension(IValueNode node, Func<string, IValueNode?> variable, int depth)
    {
        if (depth > TreeSqlCompiler.MaxFilterDepth)
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        if (node is not ObjectValueNode obj)
        {
            throw InvalidFilter();
        }

        var fields = Fields(obj, variable);
        if (fields.Count != 2 ||
            !fields.TryGetValue("name", out var nameNode) || nameNode is not StringValueNode { Value: { Length: > 0 and <= MaxNameLength } name } ||
            !fields.TryGetValue("where", out var whereNode) || whereNode is not ObjectValueNode typed)
        {
            throw InvalidFilter();
        }

        // @oneOf: exactly one typed leaf.
        var leaves = Fields(typed, variable);
        if (leaves.Count != 1)
        {
            throw InvalidFilter();
        }

        var (leafName, leafValue) = leaves.Single();
        if (leafValue is not ObjectValueNode ops || ops.Fields.Count == 0 || !TryKind(leafName, out var kind))
        {
            throw InvalidFilter();
        }

        var comparisons = new List<TreeFilter>();
        foreach (var (opName, opValue) in Fields(ops, variable))
        {
            if (!TryOperator(opName, kind, out var op) || IsAbsent(opValue))
            {
                throw InvalidFilter();
            }

            comparisons.Add(new TreeComparison(name, op, OperandFor(op, kind, opValue!, variable)) { DeclaredKind = kind });
        }

        return comparisons.Count == 1 ? comparisons[0] : new TreeAndFilter(comparisons);
    }

    private static bool TryKind(string leaf, out ColumnValueKind kind)
    {
        switch (leaf)
        {
            case "string": kind = ColumnValueKind.String; return true;
            case "int": kind = ColumnValueKind.Int; return true;
            case "long": kind = ColumnValueKind.Long; return true;
            case "float": kind = ColumnValueKind.Float; return true;
            case "decimal": kind = ColumnValueKind.Decimal; return true;
            case "boolean": kind = ColumnValueKind.Boolean; return true;
            case "dateTime": kind = ColumnValueKind.DateTime; return true;
            default: kind = default; return false;
        }
    }

    private static bool TryOperator(string name, ColumnValueKind kind, out TreeFilterOperator op)
    {
        switch (name)
        {
            case "eq": op = TreeFilterOperator.Eq; return true;
            case "neq": op = TreeFilterOperator.Neq; return true;
            case "isNull": op = TreeFilterOperator.IsNull; return true;
            case "gt" or "gte" or "lt" or "lte" or "in" or "nin" when kind != ColumnValueKind.Boolean:
                op = name switch
                {
                    "gt" => TreeFilterOperator.Gt,
                    "gte" => TreeFilterOperator.Gte,
                    "lt" => TreeFilterOperator.Lt,
                    "lte" => TreeFilterOperator.Lte,
                    "in" => TreeFilterOperator.In,
                    _ => TreeFilterOperator.NotIn
                };
                return true;
            case "contains" or "startsWith" or "endsWith" when kind == ColumnValueKind.String:
                op = name switch
                {
                    "contains" => TreeFilterOperator.Contains,
                    "startsWith" => TreeFilterOperator.StartsWith,
                    _ => TreeFilterOperator.EndsWith
                };
                return true;
            default:
                op = default;
                return false;
        }
    }

    private static object? OperandFor(TreeFilterOperator op, ColumnValueKind kind, IValueNode value, Func<string, IValueNode?> variable)
    {
        if (op == TreeFilterOperator.IsNull)
        {
            return value is BooleanValueNode b ? b.Value : throw InvalidFilter();
        }

        if (op is TreeFilterOperator.In or TreeFilterOperator.NotIn)
        {
            return Items(value, variable).Select(i => (object?)Scalar(kind, i)).ToList();
        }

        return Scalar(kind, value);
    }

    private static object Scalar(ColumnValueKind kind, IValueNode node)
    {
        switch (kind)
        {
            case ColumnValueKind.String or ColumnValueKind.DateTime when node is StringValueNode s:
                return s.Value;
            case ColumnValueKind.Int or ColumnValueKind.Long when node is IntValueNode i &&
                                                                 long.TryParse(i.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l):
                return l;
            case ColumnValueKind.Float when node is FloatValueNode or IntValueNode &&
                                           double.TryParse(((IValueNode<string>)node).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                                           double.IsFinite(d):
                return d;
            case ColumnValueKind.Decimal when node is FloatValueNode or IntValueNode &&
                                             decimal.TryParse(((IValueNode<string>)node).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m):
                return m;
            case ColumnValueKind.Boolean when node is BooleanValueNode b:
                return b.Value;
            default:
                throw InvalidFilter();
        }
    }

    private static List<TableOrderBy>? ParseOrderBy(IValueNode? node, Func<string, IValueNode?> variable, int max)
    {
        var resolved = Resolve(node, variable);
        if (IsAbsent(resolved))
        {
            return null;
        }

        var items = Items(resolved!, variable);
        if (items.Count > max)
        {
            throw AggregateErrors.OrderInvalid();
        }

        var result = new List<TableOrderBy>(items.Count);
        foreach (var item in items)
        {
            if (item is not ObjectValueNode obj)
            {
                throw AggregateErrors.OrderInvalid();
            }

            var fields = Fields(obj, variable);
            var hasDimension = fields.TryGetValue("dimension", out var dimension) && !IsAbsent(dimension);
            var hasMetric = fields.TryGetValue("metric", out var metric) && !IsAbsent(metric);
            if (hasDimension == hasMetric)
            {
                throw AggregateErrors.OrderInvalid();
            }

            var target = (hasDimension ? dimension : metric) as StringValueNode;
            if (target is null || target.Value.Length is 0 or > MaxNameLength)
            {
                throw AggregateErrors.OrderInvalid();
            }

            var descending = false;
            if (fields.TryGetValue("direction", out var direction) && !IsAbsent(direction))
            {
                var name = direction switch { EnumValueNode e => e.Value, StringValueNode s => s.Value, _ => null };
                descending = name switch
                {
                    "DESC" => true,
                    "ASC" => false,
                    _ => throw AggregateErrors.OrderInvalid()
                };
            }

            result.Add(new TableOrderBy(target.Value, descending));
        }

        return result;
    }

    private static int? ParseLimit(IValueNode? node, Func<string, IValueNode?> variable)
    {
        var resolved = Resolve(node, variable);
        if (IsAbsent(resolved))
        {
            return null;
        }

        if (resolved is not IntValueNode intNode ||
            !long.TryParse(intNode.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        return value > AggregationOptions.UpperBoundMaxGroups ? throw AggregateErrors.LimitExceeded() : (int)value;
    }
}
