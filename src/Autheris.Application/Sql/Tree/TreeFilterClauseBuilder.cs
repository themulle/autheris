using System.Globalization;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;

namespace Autheris.Application.Sql.Tree;

/// <summary>A catalog column as the filter builder sees it: the catalog spelling and the kind its values are coerced to.</summary>
public sealed record ColumnRef(string CatalogName, ColumnValueKind Kind);

/// <summary>
/// Turns a typed filter tree into one parameterized <see cref="TableFilterClause"/> in Trino syntax (#112 A-3). One builder serves
/// the typed aggregate field and the typed metric query. The predicate text never contains a literal: every value is a named
/// parameter. Columns are resolved by the caller; an unresolvable column fails with the uniform
/// <see cref="AggregationColumnUnavailableException"/>.
/// </summary>
public static class TreeFilterClauseBuilder
{
    /// <summary>
    /// Prefix of the generated parameter names. It is a policy-marker prefix (<c>__gql_</c>) so that the in-memory evaluator of the
    /// executor branch binds the values; the infix keeps it apart from the row-filter markers (<c>rls</c>, <c>vf</c>).
    /// </summary>
    public const string ParameterPrefix = "@__gql_tf_";

    /// <summary>B-2: most comparison leaves one filter tree may carry (an IN list is one leaf).</summary>
    public const int MaxLeafCount = 64;

    /// <summary>B-2: longest string value (characters) one filter value may carry.</summary>
    public const int MaxStringValueLength = 1_024;

    public static TableFilterClause Build(TreeFilter filter, Func<string, ColumnRef?> resolveColumn)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(resolveColumn);

        // Resolve and coerce once into a normalized tree: referenced columns and values are then exact and render-independent.
        var referenced = new List<string>();
        var leaves = 0;
        var normalized = Normalize(filter, resolveColumn, referenced, 0, ref leaves);

        // SqlPredicate is the form the in-memory evaluator reads (no redundant ESCAPE clause); the Trino factory always carries it.
        var identity = new BuilderTarget(static c => c, alwaysEmitLikeEscape: false);
        var predicate = TreeFilterRenderer.Render(normalized, identity);

        return new TableFilterClause(
            predicate,
            identity.Parameters,
            referenced.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            TrinoSqlFactory = resolve => TreeFilterRenderer.Render(normalized, new BuilderTarget(resolve, alwaysEmitLikeEscape: true))
        };
    }

    private static TreeFilter Normalize(TreeFilter filter, Func<string, ColumnRef?> resolve, List<string> referenced, int depth, ref int leaves)
    {
        if (depth > TreeSqlCompiler.MaxFilterDepth)
        {
            throw new Autheris.Domain.Exceptions.GatewayInvalidQueryException(
                $"The filter is nested deeper than {TreeSqlCompiler.MaxFilterDepth} levels.");
        }

        switch (filter)
        {
            case TreeAndFilter and:
                return new TreeAndFilter(NormalizeItems(and.Items, resolve, referenced, depth, ref leaves));
            case TreeOrFilter or:
                return new TreeOrFilter(NormalizeItems(or.Items, resolve, referenced, depth, ref leaves));
            case TreeNotFilter not:
                return new TreeNotFilter(Normalize(not.Item, resolve, referenced, depth + 1, ref leaves));
            case TreeComparison comparison:
            {
                if (++leaves > MaxLeafCount)
                {
                    throw new Autheris.Domain.Exceptions.GatewayInvalidQueryException(
                        $"The filter has more than {MaxLeafCount} conditions.");
                }

                EnsureStringLength(comparison.Value);
                var column = resolve(comparison.Column) ?? throw new AggregationColumnUnavailableException();
                referenced.Add(column.CatalogName);
                var value = FilterValueCoercion.Coerce(comparison.Value, column.Kind, comparison.Operator);
                return new TreeComparison(column.CatalogName, comparison.Operator, value);
            }
            default:
                throw new Autheris.Domain.Exceptions.GatewayInvalidQueryException("Unsupported filter.");
        }
    }

    private static List<TreeFilter> NormalizeItems(
        IReadOnlyList<TreeFilter> items, Func<string, ColumnRef?> resolve, List<string> referenced, int depth, ref int leaves)
    {
        var result = new List<TreeFilter>(items.Count);
        foreach (var item in items)
        {
            result.Add(Normalize(item, resolve, referenced, depth + 1, ref leaves));
        }

        return result;
    }

    private static void EnsureStringLength(object? value)
    {
        switch (value)
        {
            case string s when s.Length > MaxStringValueLength:
                throw new Autheris.Domain.Exceptions.GatewayInvalidQueryException(
                    $"A filter value is longer than {MaxStringValueLength} characters.");
            case System.Collections.IEnumerable list when value is not string:
                foreach (var entry in list)
                {
                    EnsureStringLength(entry);
                }

                break;
        }
    }

    private sealed class BuilderTarget(Func<string, string> resolveSpelling, bool alwaysEmitLikeEscape) : ITreeFilterTarget
    {
        private int _counter;

        public Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);

        public string Column(string column) => DataApiFilterTranslator.QuoteIdentifier(resolveSpelling(column));

        public string AddParameter(object? value)
        {
            var name = ParameterPrefix + (_counter++).ToString(CultureInfo.InvariantCulture);
            Parameters[name] = value;
            return name;
        }

        // Trino rejects a backslash in string literals and an escape character before a non-special character, so '!' escapes only
        // itself, '%' and '_' (ODataFilterParser uses the same escape character).
        public char LikeEscape => '!';

        public string LikeSpecials => "%_";

        public bool AlwaysEmitLikeEscape => alwaysEmitLikeEscape;
    }
}
