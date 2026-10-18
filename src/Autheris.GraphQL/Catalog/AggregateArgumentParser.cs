using System.Globalization;
using System.Text.RegularExpressions;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using HotChocolate.Language;

namespace Autheris.GraphQL.Catalog;

/// <summary>The validated arguments of one <c>T_aggregate</c> call (the typed <c>where</c> is parsed separately).</summary>
internal sealed record ParsedAggregateArguments(
    IReadOnlyList<CatalogColumnField> GroupBy,
    IReadOnlyList<ResolvedAggregate> Aggregates,
    IReadOnlyList<TableOrderBy> OrderBy,
    int Limit);

/// <summary>
/// #112 A-2 / A-4 / C-4: argument shape, alias rules and the function/type matrix, decided before the engine is called. Errors carry
/// fixed messages and stable codes and never echo a client value.
/// </summary>
internal static partial class AggregateArgumentParser
{
    public const int DefaultLimit = 100;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AliasPattern();

    public static ParsedAggregateArguments Parse(
        CatalogTableType table,
        IValueNode? groupBy,
        IValueNode? aggregates,
        IValueNode? orderBy,
        IValueNode? limit,
        AggregationOptions options,
        Func<string, IValueNode?> variable)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(variable);

        var columns = AggregateColumnEnum.Build(table).ToDictionary(e => e.EnumValue, e => e.Column, StringComparer.Ordinal);

        var groupColumns = ParseGroupBy(groupBy, columns, options, variable);
        var parsedAggregates = ParseAggregates(aggregates, columns, groupColumns, options, variable);
        var order = ParseOrderBy(orderBy, columns, groupColumns, parsedAggregates, options, variable);
        var parsedLimit = ParseLimit(limit, variable);

        return new ParsedAggregateArguments(groupColumns, parsedAggregates, order, parsedLimit);
    }

    private static IValueNode? Resolve(IValueNode? node, Func<string, IValueNode?> variable) =>
        node is VariableNode v ? variable(v.Name.Value) : node;

    private static bool IsAbsent(IValueNode? node) => node is null or NullValueNode;

    private static IReadOnlyList<IValueNode> Items(IValueNode node, Func<string, IValueNode?> variable) => node switch
    {
        ListValueNode list => list.Items.Select(i => Resolve(i, variable) ?? NullValueNode.Default).ToList(),
        // GraphQL input coercion: a single value stands for a one-element list.
        _ => [node]
    };

    private static string EnumName(IValueNode? node, Func<string, IValueNode?> variable) =>
        Resolve(node, variable) switch
        {
            EnumValueNode e => e.Value,
            StringValueNode s => s.Value,
            _ => throw AggregateErrors.ArgumentInvalid()
        };

    private static List<CatalogColumnField> ParseGroupBy(
        IValueNode? node,
        IReadOnlyDictionary<string, CatalogColumnField> columns,
        AggregationOptions options,
        Func<string, IValueNode?> variable)
    {
        var result = new List<CatalogColumnField>();
        var resolved = Resolve(node, variable);
        if (IsAbsent(resolved))
        {
            return result;
        }

        var items = Items(resolved!, variable);
        if (items.Count > options.MaxGroupByColumns)
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        foreach (var item in items)
        {
            if (!columns.TryGetValue(EnumName(item, variable), out var column) || result.Contains(column))
            {
                throw AggregateErrors.ArgumentInvalid();
            }

            result.Add(column);
        }

        return result;
    }

    private static List<ResolvedAggregate> ParseAggregates(
        IValueNode? node,
        IReadOnlyDictionary<string, CatalogColumnField> columns,
        IReadOnlyList<CatalogColumnField> groupColumns,
        AggregationOptions options,
        Func<string, IValueNode?> variable)
    {
        var resolved = Resolve(node, variable);
        if (IsAbsent(resolved))
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        var items = Items(resolved!, variable);
        if (items.Count < 1 || items.Count > options.MaxAggregates)
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        var shapes = new List<(string Alias, AggregateFunction Function, CatalogColumnField? Column)>(items.Count);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is not ObjectValueNode obj)
            {
                throw AggregateErrors.ArgumentInvalid();
            }

            var fields = obj.Fields.ToDictionary(f => f.Name.Value, f => Resolve(f.Value, variable), StringComparer.Ordinal);
            if (!fields.TryGetValue("fn", out var fnNode) || !TryParseFunction(EnumName(fnNode, variable), out var function))
            {
                throw AggregateErrors.ArgumentInvalid();
            }

            CatalogColumnField? column = null;
            if (fields.TryGetValue("column", out var colNode) && !IsAbsent(colNode) &&
                !columns.TryGetValue(EnumName(colNode, variable), out column))
            {
                throw AggregateErrors.ArgumentInvalid();
            }

            if (!fields.TryGetValue("alias", out var aliasNode) || aliasNode is not StringValueNode aliasValue)
            {
                throw AggregateErrors.AliasInvalid();
            }

            var alias = aliasValue.Value;
            if (!AliasPattern().IsMatch(alias) ||
                string.Equals(alias, "count", StringComparison.OrdinalIgnoreCase) ||
                alias.StartsWith("__", StringComparison.Ordinal) ||
                !aliases.Add(alias) ||
                groupColumns.Any(g => string.Equals(g.ColumnName, alias, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(g.FieldName, alias, StringComparison.OrdinalIgnoreCase)))
            {
                throw AggregateErrors.AliasInvalid();
            }

            shapes.Add((alias, function, column));
        }

        // C-4: the same (function, column) pair twice in one call is rejected, whatever the aliases are.
        var pairs = new HashSet<(AggregateFunction, string)>();
        foreach (var shape in shapes)
        {
            if (!pairs.Add((shape.Function, shape.Column?.FieldName.ToLowerInvariant() ?? string.Empty)))
            {
                throw AggregateErrors.ArgumentInvalid();
            }
        }

        var result = new List<ResolvedAggregate>(shapes.Count);
        foreach (var (alias, function, column) in shapes)
        {
            var kind = AggregateResultMapper.KindFor(function, column?.FieldType) ?? throw AggregateErrors.InvalidFunctionForType();
            result.Add(new ResolvedAggregate(alias, function, column, kind));
        }

        return result;
    }

    private static bool TryParseFunction(string name, out AggregateFunction function)
    {
        switch (name)
        {
            case "COUNT": function = AggregateFunction.Count; return true;
            case "COUNT_DISTINCT": function = AggregateFunction.CountDistinct; return true;
            case "SUM": function = AggregateFunction.Sum; return true;
            case "AVG": function = AggregateFunction.Avg; return true;
            case "MIN": function = AggregateFunction.Min; return true;
            case "MAX": function = AggregateFunction.Max; return true;
            default: function = default; return false;
        }
    }

    private static List<TableOrderBy> ParseOrderBy(
        IValueNode? node,
        IReadOnlyDictionary<string, CatalogColumnField> columns,
        IReadOnlyList<CatalogColumnField> groupColumns,
        IReadOnlyList<ResolvedAggregate> aggregates,
        AggregationOptions options,
        Func<string, IValueNode?> variable)
    {
        var result = new List<TableOrderBy>();
        var resolved = Resolve(node, variable);
        if (IsAbsent(resolved))
        {
            return result;
        }

        var items = Items(resolved!, variable);
        if (items.Count > options.MaxGroupByColumns + options.MaxAggregates)
        {
            throw AggregateErrors.OrderInvalid();
        }

        foreach (var item in items)
        {
            if (item is not ObjectValueNode obj)
            {
                throw AggregateErrors.OrderInvalid();
            }

            var fields = obj.Fields.ToDictionary(f => f.Name.Value, f => Resolve(f.Value, variable), StringComparer.Ordinal);
            var hasColumn = fields.TryGetValue("column", out var colNode) && !IsAbsent(colNode);
            var hasAlias = fields.TryGetValue("alias", out var aliasNode) && !IsAbsent(aliasNode);
            if (hasColumn == hasAlias)
            {
                throw AggregateErrors.OrderInvalid();
            }

            var descending = false;
            if (fields.TryGetValue("direction", out var dirNode) && !IsAbsent(dirNode))
            {
                var direction = EnumName(dirNode, variable);
                if (direction == "DESC")
                {
                    descending = true;
                }
                else if (direction != "ASC")
                {
                    throw AggregateErrors.OrderInvalid();
                }
            }

            if (hasColumn)
            {
                if (!columns.TryGetValue(EnumName(colNode, variable), out var column) || !groupColumns.Contains(column))
                {
                    throw AggregateErrors.OrderInvalid();
                }

                result.Add(new TableOrderBy(column.ColumnName, descending));
            }
            else
            {
                if (aliasNode is not StringValueNode aliasValue ||
                    aggregates.FirstOrDefault(a => string.Equals(a.Alias, aliasValue.Value, StringComparison.OrdinalIgnoreCase)) is not { } aggregate)
                {
                    throw AggregateErrors.OrderInvalid();
                }

                result.Add(new TableOrderBy(aggregate.Alias, descending));
            }
        }

        return result;
    }

    private static int ParseLimit(IValueNode? node, Func<string, IValueNode?> variable)
    {
        var resolved = Resolve(node, variable);
        if (IsAbsent(resolved))
        {
            return DefaultLimit;
        }

        if (resolved is not IntValueNode intNode ||
            !long.TryParse(intNode.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        if (value <= 0)
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        if (value > AggregationOptions.UpperBoundMaxGroups)
        {
            throw AggregateErrors.LimitExceeded();
        }

        return (int)value;
    }
}
