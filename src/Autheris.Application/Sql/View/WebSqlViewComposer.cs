namespace Autheris.Application.Sql.View;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.Sql.Tree;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Applies an optional structured <c>view</c> (filter, sort, total count) to a fixed stored statement. The view is a closed model, never SQL:
/// it is compiled to Trino text that wraps the stored statement as a derived table, <c>SELECT * FROM (stored) AS v WHERE ... ORDER BY ...</c>.
/// The wrapped text then runs through the same governed pipeline as any statement, so row rules, tenant, consent, masks, the coverage proof,
/// the cost gate and the audit apply to the stored statement unchanged, and the outer query can read nothing but the derived table.
/// Columns are resolved against the output columns the stored statement names; every value is a bound parameter.
/// </summary>
public static class WebSqlViewComposer
{
    public const int MaxFilterDepth = TreeSqlCompiler.MaxFilterDepth;
    public const int MaxFilterLeaves = TreeFilterClauseBuilder.MaxLeafCount;
    public const int MaxStringValueLength = TreeFilterClauseBuilder.MaxStringValueLength;
    public const int MaxOrderByColumns = 8;
    public const int MaxViewJsonDepth = 32;

    private const string DerivedAlias = "v";
    private const string TotalCountColumn = "total_count";

    /// <summary>
    /// Parses and validates <paramref name="view"/> against the output columns of <paramref name="storedSql"/> and returns the plan.
    /// Throws <see cref="WebSqlViewException"/> for anything that is not a valid view of this statement (fail closed).
    /// </summary>
    public static WebSqlViewPlan Plan(string storedSql, JsonElement view, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storedSql);

        if (view.ValueKind != JsonValueKind.Object)
        {
            throw new WebSqlViewException("The view must be an object.");
        }

        var outputs = DescribeStoredStatement(storedSql, out var innerSql, cancellationToken);
        var parsed = ParseView(view);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var target = new ViewTarget(outputs, parameters);
        string? where = parsed.Filter is null ? null : TreeFilterRenderer.Render(parsed.Filter, target);

        var orderBy = new StringBuilder();
        foreach (var order in parsed.OrderBy)
        {
            if (orderBy.Length > 0)
            {
                orderBy.Append(", ");
            }

            orderBy.Append(target.Column(order.Column)).Append(order.Descending ? " DESC" : " ASC");
        }

        string source = "FROM (\n" + innerSql + "\n) AS " + DerivedAlias;
        var select = new StringBuilder("SELECT * ").Append(source);
        var count = new StringBuilder("SELECT COUNT(*) AS ").Append(TotalCountColumn).Append(' ').Append(source);
        if (where is not null)
        {
            select.Append(" WHERE ").Append(where);
            count.Append(" WHERE ").Append(where);
        }

        if (orderBy.Length > 0)
        {
            select.Append(" ORDER BY ").Append(orderBy);
        }

        return new WebSqlViewPlan(select.ToString(), parsed.TotalCount ? count.ToString() : null, parameters);
    }

    /// <summary>
    /// Validates that the stored statement is one SELECT whose output columns are all named, and returns them. The statement text is the
    /// authority of what can be read; nothing is added to it here.
    /// </summary>
    private static IReadOnlyList<OutputColumn> DescribeStoredStatement(string storedSql, out string innerSql, CancellationToken ct)
    {
        string trimmed = SqlParameterExtractor.StripComments(storedSql).Trim().TrimEnd(';').Trim();
        if (trimmed.Length == 0)
        {
            throw new WebSqlViewException("The view needs a SELECT statement.");
        }

        if (trimmed.Contains("__gql_", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("__param_", StringComparison.OrdinalIgnoreCase))
        {
            throw new WebSqlViewException("The statement uses a reserved identifier prefix.");
        }

        SqlStatement statement;
        try
        {
            string normalized = SqlParameterExtractor.NormalizeForAst(trimmed);
            var (tree, _) = FastSqlEngine.Default.Parse(normalized.AsMemory(), null, ct);
            statement = new SqlAstBuilder(new AstBuilderOptions { EnforceFunctionPolicy = false, EnforceReadOnlyQueries = true }).BuildStatement(tree);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The statement itself is judged by the governed compile; here only its shape matters, so any failure refuses the view.
            throw new WebSqlViewException("The view cannot be applied: the stored statement could not be read as a single SELECT.");
        }

        if (statement is not SelectStatement select)
        {
            throw new WebSqlViewException("The view can only be applied to a SELECT statement.");
        }

        if (select.OrderBy is not null && select.Pagination is null)
        {
            throw new WebSqlViewException("The view cannot be applied to a statement with ORDER BY and no LIMIT; sort with view.orderBy instead.");
        }

        var names = OutputNames(select.Body);
        if (names is null || names.Count == 0)
        {
            throw new WebSqlViewException("The view needs a statement that names its output columns (no wildcard, every expression aliased).");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in names)
        {
            if (!seen.Add(column.Name))
            {
                throw new WebSqlViewException("The view needs unique output column names.");
            }
        }

        innerSql = trimmed;
        return names;
    }

    private static List<OutputColumn>? OutputNames(QueryBody body)
    {
        switch (body)
        {
            case QuerySpecification spec:
            {
                var names = new List<OutputColumn>(spec.Projections.Count);
                foreach (var item in spec.Projections)
                {
                    if (item is not ColumnSelectItem column)
                    {
                        return null;
                    }

                    if (column.Alias is { } alias)
                    {
                        names.Add(new OutputColumn(alias.Value, alias.IsQuoted));
                    }
                    else if (column.Expression is ColumnReference reference)
                    {
                        var last = reference.Name.Parts[^1];
                        names.Add(new OutputColumn(last.Value, last.IsQuoted));
                    }
                    else
                    {
                        return null;
                    }
                }

                return names;
            }
            case SetOperationQuery setOperation:
                return OutputNames(setOperation.Left);
            default:
                return null;
        }
    }

    private static ParsedView ParseView(JsonElement view)
    {
        TreeFilter? filter = null;
        var orderBy = new List<TreeOrder>();
        bool totalCount = false;
        int leaves = 0;

        foreach (var member in view.EnumerateObject())
        {
            switch (member.Name)
            {
                case "filter":
                    filter = ParseFilter(member.Value, 0, ref leaves);
                    break;
                case "orderBy":
                    orderBy = ParseOrderBy(member.Value);
                    break;
                case "totalCount":
                    if (member.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new WebSqlViewException("view.totalCount must be true or false.");
                    }

                    totalCount = member.Value.GetBoolean();
                    break;
                default:
                    throw new WebSqlViewException("The view has a member that is not supported. Supported: filter, orderBy, totalCount.");
            }
        }

        return new ParsedView(filter, orderBy, totalCount);
    }

    private static List<TreeOrder> ParseOrderBy(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new WebSqlViewException("view.orderBy must be an array.");
        }

        var result = new List<TreeOrder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray())
        {
            if (result.Count >= MaxOrderByColumns)
            {
                throw new WebSqlViewException($"view.orderBy has more than {MaxOrderByColumns} entries.");
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new WebSqlViewException("Each view.orderBy entry must be an object.");
            }

            string? column = null;
            bool descending = false;
            foreach (var member in item.EnumerateObject())
            {
                switch (member.Name)
                {
                    case "column" when member.Value.ValueKind == JsonValueKind.String:
                        column = member.Value.GetString();
                        break;
                    case "direction" when member.Value.ValueKind == JsonValueKind.String:
                        descending = member.Value.GetString() switch
                        {
                            "asc" => false,
                            "desc" => true,
                            _ => throw new WebSqlViewException("view.orderBy direction must be 'asc' or 'desc'.")
                        };
                        break;
                    default:
                        throw new WebSqlViewException("A view.orderBy entry has a member that is not supported. Supported: column, direction.");
                }
            }

            if (string.IsNullOrEmpty(column))
            {
                throw new WebSqlViewException("Each view.orderBy entry needs a column.");
            }

            if (!seen.Add(column))
            {
                throw new WebSqlViewException("A column may appear only once in view.orderBy.");
            }

            result.Add(new TreeOrder(column, descending));
        }

        return result;
    }

    private static TreeFilter ParseFilter(JsonElement node, int depth, ref int leaves)
    {
        if (depth > MaxFilterDepth)
        {
            throw new WebSqlViewException($"The view filter is nested deeper than {MaxFilterDepth} levels.");
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new WebSqlViewException("A view filter node must be an object.");
        }

        string? column = null;
        string? op = null;
        JsonElement? value = null;
        JsonElement? values = null;
        JsonElement? and = null;
        JsonElement? or = null;
        JsonElement? not = null;
        int members = 0;
        foreach (var member in node.EnumerateObject())
        {
            members++;
            switch (member.Name)
            {
                case "and":
                    and = member.Value;
                    break;
                case "or":
                    or = member.Value;
                    break;
                case "not":
                    not = member.Value;
                    break;
                case "column" when member.Value.ValueKind == JsonValueKind.String:
                    column = member.Value.GetString();
                    break;
                case "op" when member.Value.ValueKind == JsonValueKind.String:
                    op = member.Value.GetString();
                    break;
                case "value":
                    value = member.Value;
                    break;
                case "values":
                    values = member.Value;
                    break;
                default:
                    throw new WebSqlViewException("A view filter node has a member that is not supported.");
            }
        }

        bool isGroup = and is not null || or is not null || not is not null;
        if (isGroup)
        {
            if (members != 1)
            {
                throw new WebSqlViewException("A view filter group must have exactly one of and, or, not.");
            }

            if (not is { } negated)
            {
                return new TreeNotFilter(ParseFilter(negated, depth + 1, ref leaves));
            }

            var list = and ?? or!.Value;
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                throw new WebSqlViewException("A view filter group needs a non-empty array.");
            }

            var items = new List<TreeFilter>(list.GetArrayLength());
            foreach (var child in list.EnumerateArray())
            {
                items.Add(ParseFilter(child, depth + 1, ref leaves));
            }

            return and is not null ? new TreeAndFilter(items) : new TreeOrFilter(items);
        }

        if (++leaves > MaxFilterLeaves)
        {
            throw new WebSqlViewException($"The view filter has more than {MaxFilterLeaves} conditions.");
        }

        if (string.IsNullOrEmpty(column) || string.IsNullOrEmpty(op))
        {
            throw new WebSqlViewException("A view filter condition needs a column and an op.");
        }

        return ParseCondition(column, op, value, values);
    }

    private static TreeFilter ParseCondition(string column, string op, JsonElement? value, JsonElement? values)
    {
        switch (op)
        {
            case "isNull":
            case "isNotNull":
                RequireNoOperand(value, values);
                return new TreeComparison(column, TreeFilterOperator.IsNull, op == "isNull");
            case "eq":
                return new TreeComparison(column, TreeFilterOperator.Eq, RequireScalar(value, values));
            case "ne":
                return new TreeComparison(column, TreeFilterOperator.Neq, RequireScalar(value, values));
            case "gt":
                return new TreeComparison(column, TreeFilterOperator.Gt, RequireScalar(value, values));
            case "gte":
                return new TreeComparison(column, TreeFilterOperator.Gte, RequireScalar(value, values));
            case "lt":
                return new TreeComparison(column, TreeFilterOperator.Lt, RequireScalar(value, values));
            case "lte":
                return new TreeComparison(column, TreeFilterOperator.Lte, RequireScalar(value, values));
            case "contains":
                return new TreeComparison(column, TreeFilterOperator.Contains, RequireText(value, values));
            case "startsWith":
                return new TreeComparison(column, TreeFilterOperator.StartsWith, RequireText(value, values));
            case "endsWith":
                return new TreeComparison(column, TreeFilterOperator.EndsWith, RequireText(value, values));
            case "in":
                return new TreeComparison(column, TreeFilterOperator.In, RequireList(value, values, 1));
            case "notIn":
                return new TreeComparison(column, TreeFilterOperator.NotIn, RequireList(value, values, 1));
            case "between":
            {
                var bounds = RequireList(value, values, 2);
                if (bounds.Count != 2)
                {
                    throw new WebSqlViewException("The op 'between' needs exactly two values.");
                }

                return new TreeAndFilter(
                [
                    new TreeComparison(column, TreeFilterOperator.Gte, bounds[0]),
                    new TreeComparison(column, TreeFilterOperator.Lte, bounds[1])
                ]);
            }
            default:
                throw new WebSqlViewException("A view filter uses an op that is not supported.");
        }
    }

    private static void RequireNoOperand(JsonElement? value, JsonElement? values)
    {
        if (value is not null || values is not null)
        {
            throw new WebSqlViewException("The ops isNull and isNotNull take no value.");
        }
    }

    private static object RequireScalar(JsonElement? value, JsonElement? values)
    {
        if (values is not null || value is null)
        {
            throw new WebSqlViewException("This op needs a single 'value'.");
        }

        return ToScalar(value.Value);
    }

    private static string RequireText(JsonElement? value, JsonElement? values)
    {
        if (values is not null || value is not { ValueKind: JsonValueKind.String } text)
        {
            throw new WebSqlViewException("This op needs a text 'value'.");
        }

        return CheckLength(text.GetString()!);
    }

    private static List<object?> RequireList(JsonElement? value, JsonElement? values, int minimum)
    {
        if (value is not null || values is not { ValueKind: JsonValueKind.Array } array)
        {
            throw new WebSqlViewException("This op needs an array 'values'.");
        }

        int count = array.GetArrayLength();
        if (count < minimum)
        {
            throw new WebSqlViewException("This op needs at least one value.");
        }

        if (count > TreeSqlCompiler.MaxInListSize)
        {
            throw new WebSqlViewException($"A list may contain at most {TreeSqlCompiler.MaxInListSize} values.");
        }

        var list = new List<object?>(count);
        foreach (var item in array.EnumerateArray())
        {
            list.Add(ToScalar(item));
        }

        return list;
    }

    /// <summary>Strings, numbers and booleans only. A null, an object or an array is never a filter value (use isNull / isNotNull).</summary>
    private static object ToScalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => CheckLength(element.GetString()!),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetInt64(out long l) => l,
        JsonValueKind.Number when element.TryGetDecimal(out decimal d) => d,
        _ => throw new WebSqlViewException("A filter value must be a string, a number or a boolean.")
    };

    private static string CheckLength(string text) =>
        text.Length > MaxStringValueLength
            ? throw new WebSqlViewException($"A filter value is longer than {MaxStringValueLength} characters.")
            : text;

    private readonly record struct OutputColumn(string Name, bool Quoted);

    private sealed record ParsedView(TreeFilter? Filter, List<TreeOrder> OrderBy, bool TotalCount);

    /// <summary>Resolves a column name only against the stored statement's output columns and binds values under gateway-owned names.</summary>
    private sealed class ViewTarget(IReadOnlyList<OutputColumn> outputs, Dictionary<string, object?> parameters) : ITreeFilterTarget
    {
        private int _counter;

        public string Column(string column)
        {
            OutputColumn? match = null;
            foreach (var output in outputs)
            {
                if (string.Equals(output.Name, column, StringComparison.Ordinal))
                {
                    match = output;
                    break;
                }

                if (string.Equals(output.Name, column, StringComparison.OrdinalIgnoreCase))
                {
                    match = output;
                }
            }

            if (match is not { } found)
            {
                throw WebSqlViewException.ForColumn("The view names a column that is not an output column of the statement:", column);
            }

            return DerivedAlias + "." + (found.Quoted || !IsPlainIdentifier(found.Name)
                ? "\"" + found.Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : found.Name);
        }

        public string AddParameter(object? value)
        {
            if (parameters.Count >= TreeSqlCompiler.MaxParameters)
            {
                throw new WebSqlViewException($"The view uses more than {TreeSqlCompiler.MaxParameters} values.");
            }

            string name = TreeFilterClauseBuilder.ParameterPrefix + (_counter++).ToString(CultureInfo.InvariantCulture);
            parameters[name] = value;
            return name;
        }

        public char LikeEscape => '!';

        public string LikeSpecials => "%_";

        public bool AlwaysEmitLikeEscape => true;

        private static bool IsPlainIdentifier(string name)
        {
            if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
            {
                return false;
            }

            foreach (char c in name)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

/// <summary>The compiled view of one request: the wrapped statements and the gateway-owned values they bind.</summary>
public sealed class WebSqlViewPlan
{
    internal WebSqlViewPlan(string sql, string? countSql, IReadOnlyDictionary<string, object?> parameters)
    {
        Sql = sql;
        CountSql = countSql;
        Parameters = parameters;
    }

    /// <summary>The stored statement wrapped with the view's filter and sort. Contains no value, only bind names.</summary>
    public string Sql { get; }

    /// <summary>The wrapped total count statement, when the view asked for <c>totalCount</c>; null otherwise.</summary>
    public string? CountSql { get; }

    internal IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>The request that returns the rows of the view. The values travel as gateway parameters, never in the text.</summary>
    public GovernedSqlQueryRequest Apply(GovernedSqlQueryRequest request) => Apply(request, Sql);

    /// <summary>The request that counts the rows of the view (no sort, no paging); null without <c>totalCount</c>.</summary>
    public GovernedSqlQueryRequest? ApplyCount(GovernedSqlQueryRequest request) =>
        CountSql is null
            ? null
            : Apply(request with { Page = null, NextToken = null, OnPageOutcome = null, OnIndexHints = null, ResultCache = null }, CountSql);

    private GovernedSqlQueryRequest Apply(GovernedSqlQueryRequest request, string sql) =>
        request with { Sql = sql } with
        {
            GatewayParameters = Parameters.Count == 0 ? null : Parameters,
            StructuredViewRead = true
        };
}
