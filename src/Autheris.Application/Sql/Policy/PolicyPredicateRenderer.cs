namespace Autheris.Application.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// Renders a <see cref="PolicyPredicate"/> through the dialect generator of the AST compiler, so text and AST cannot drift. Each
/// policy parameter becomes the named marker <c>@__gql_rls_N</c> (the shape the string consumers already bind) and its value goes into
/// <see cref="RenderedPolicyPredicate.Parameters"/>; no value is ever rendered into the text.
/// </summary>
public sealed partial class PolicyPredicateRenderer : IPolicyPredicateRenderer
{
    private const string Marker = "\u0001policy\u0001";

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SafeNameRegex();

    public RenderedPolicyPredicate Render(PolicyPredicate predicate, TargetSqlDialect dialect, string targetAlias)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(targetAlias);
        if (!SafeNameRegex().IsMatch(targetAlias))
        {
            throw new ArgumentException("The target alias must be a plain identifier.", nameof(targetAlias));
        }

        var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var rewritten = (Expression)new MarkerRewriter(targetAlias, predicate.Parameters, parameters).Visit(predicate.Expression);

        // The expression is emitted inside a one-table SELECT so the generator applies its normal predicate rules; the constant
        // prefix and suffix are measured with a marker and cut off again.
        string Emit(Expression where) => generator.GenerateSql(new SelectStatement(null,
            new QuerySpecification(false, [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)],
                new NamedTableSource(new SqlQualifiedName("policy_probe"), null), where, null, null), null, null));

        var framed = Emit(new TrustedSqlExpression(Marker));
        int at = framed.IndexOf(Marker, StringComparison.Ordinal);
        var prefix = framed[..at];
        var suffix = framed[(at + Marker.Length)..];
        var sql = Emit(rewritten);
        if (!sql.StartsWith(prefix, StringComparison.Ordinal) || !sql.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The dialect generator produced an unexpected statement frame for the row predicate.");
        }

        return new RenderedPolicyPredicate(sql[prefix.Length..^suffix.Length], parameters);
    }

    private sealed class MarkerRewriter(string targetAlias, IReadOnlyDictionary<string, PolicyValue> values, Dictionary<string, object?> output) : SqlAstRewriter
    {
        public override SqlNode Visit(SqlNode node)
        {
            switch (node)
            {
                case PolicyParameterExpression parameter:
                    if (!SafeNameRegex().IsMatch(parameter.Name) || !values.TryGetValue(parameter.Name, out var value))
                    {
                        // Fail closed: a parameter node without a value (or with an unsafe name) is never rendered.
                        throw new InvalidOperationException("The row predicate references a policy parameter without a valid value.");
                    }

                    var marker = "@" + parameter.Name;
                    output[marker] = value.Value;
                    return new TrustedSqlExpression(marker);
                case ColumnReference { Name.Parts.Count: > 1 } column
                    when string.Equals(column.Name.Parts[0].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase):
                    var parts = new List<SqlIdentifier>(column.Name.Parts) { [0] = column.Name.Parts[0] with { Value = targetAlias } };
                    return column with { Name = new SqlQualifiedName(parts) };
                default:
                    return base.Visit(node);
            }
        }
    }
}
