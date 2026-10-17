namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>
/// The caller placeholder of sql Virtual Filters (PLAN-GOV-129 A-4, BC-129-05, BC-129-06): <c>{{caller.&lt;name&gt;}}</c>, unquoted, only as a
/// scalar operand of <c>= &lt;&gt; &lt; &lt;= &gt; &gt;=</c> or as the sole item of <c>IN ( ... )</c> / <c>NOT IN ( ... )</c>, and only inside a top-level
/// conjunct of the WHERE clause (never under OR, NOT, CASE, a function, LIKE or BETWEEN). The fragment is rewritten before parsing to the
/// reserved quoted sentinel <c>'__autheris_caller__&lt;name&gt;'</c>; a definition that spells the sentinel, or the placeholder inside a literal,
/// is refused, so an author, a client or a stored definition can never smuggle one in.
/// </summary>
public static partial class CallerPlaceholders
{
    public const string SentinelPrefix = "__autheris_caller__";
    private const string PlaceholderStart = "{{caller.";

    [GeneratedRegex(@"'(?:[^']|'')*'", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\{\{caller\.([a-z][a-z0-9_]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>True when the definition mentions a placeholder or the sentinel anywhere (also inside a literal): it must take the caller attribute path or be refused.</summary>
    public static bool Contains(string? fragment) =>
        !string.IsNullOrEmpty(fragment) &&
        (fragment.Contains(PlaceholderStart, StringComparison.OrdinalIgnoreCase) || fragment.Contains(SentinelPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The sentinel of <paramref name="name"/> as a string literal value.</summary>
    public static string Sentinel(string name) => SentinelPrefix + name;

    public static bool TryParseSentinel(object? literalValue, out string name)
    {
        name = string.Empty;
        if (literalValue is string text && text.StartsWith(SentinelPrefix, StringComparison.Ordinal) &&
            CallerAttributeDefinition.IsValidName(text[SentinelPrefix.Length..]))
        {
            name = text[SentinelPrefix.Length..];
            return true;
        }

        return false;
    }

    /// <summary>Replaces every placeholder by its quoted sentinel and lists the attribute names.</summary>
    /// <exception cref="ArgumentException">A forbidden spelling (sentinel, placeholder in a literal, malformed or upper-case placeholder, invalid name).</exception>
    public static string Rewrite(string fragment, out IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (fragment.Contains(SentinelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The definition uses a reserved name.", nameof(fragment));
        }

        var found = new List<string>();
        var result = new StringBuilder(fragment.Length + 32);
        int position = 0;
        foreach (Match literal in StringLiteral().Matches(fragment))
        {
            result.Append(RewriteSegment(fragment.AsSpan(position, literal.Index - position).ToString(), found));
            if (literal.Value.Contains(PlaceholderStart, StringComparison.OrdinalIgnoreCase) || literal.Value.Contains("{{caller", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A caller placeholder is not allowed inside a string literal.", nameof(fragment));
            }

            result.Append(literal.Value);
            position = literal.Index + literal.Length;
        }

        result.Append(RewriteSegment(fragment[position..], found));
        names = found.Distinct(StringComparer.Ordinal).ToList();
        return result.ToString();
    }

    private static string RewriteSegment(string segment, List<string> names)
    {
        var rewritten = Placeholder().Replace(segment, m =>
        {
            var name = m.Groups[1].Value;
            if (!CallerAttributeDefinition.IsValidName(name))
            {
                throw new ArgumentException("The caller attribute name is not valid.");
            }

            names.Add(name);
            return "'" + SentinelPrefix + name + "'";
        });
        if (rewritten.Contains("{{", StringComparison.Ordinal) || rewritten.Contains("}}", StringComparison.Ordinal))
        {
            throw new ArgumentException("A caller placeholder is malformed.");
        }

        return rewritten;
    }

    /// <summary>The definition with every sentinel replaced by a harmless literal (used for the translation check of <c>Validate</c>).</summary>
    internal static string WithHarmlessValues(string rewritten) =>
        Regex.Replace(rewritten, "'" + SentinelPrefix + "[a-z0-9_]+'", "'validation'", RegexOptions.CultureInvariant);

    /// <summary>
    /// Proves the position grammar on the parsed statement: every sentinel literal sits in a top-level conjunct of <paramref name="where"/> as one
    /// operand of a comparison or as the sole item of an IN list. Returns the attribute names with their form (<c>true</c> = IN list).
    /// </summary>
    /// <exception cref="ArgumentException">A sentinel sits anywhere else.</exception>
    internal static IReadOnlyList<(string Name, bool InList)> EnforcePositions(SqlNode statement, Expression where)
    {
        int total = CountSentinels(statement);
        var matched = new List<(string, bool)>();
        Collect(where, matched);
        if (matched.Count != total)
        {
            throw new ArgumentException(
                "A caller placeholder is only allowed as an operand of = <> < <= > >= or as the only item of IN / NOT IN, in a top-level AND conjunct of WHERE.");
        }

        return matched;
    }

    private static void Collect(Expression expression, List<(string, bool)> matched)
    {
        switch (expression)
        {
            case ParenthesizedExpression p:
                Collect(p.Expression, matched);
                break;
            case BinaryExpression { Operator: BinaryOperator.And } and:
                Collect(and.Left, matched);
                Collect(and.Right, matched);
                break;
            case BinaryExpression bin when bin.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan
                or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual:
                bool leftSentinel = IsSentinel(bin.Left, out var leftName);
                bool rightSentinel = IsSentinel(bin.Right, out var rightName);
                if (leftSentinel ^ rightSentinel)
                {
                    var other = leftSentinel ? bin.Right : bin.Left;
                    if (CountSentinels(other) == 0)
                    {
                        matched.Add((leftSentinel ? leftName : rightName, false));
                    }
                }

                break;
            case InListExpression { Items.Count: 1 } inList when IsSentinel(inList.Items[0], out var itemName) && CountSentinels(inList.Operand) == 0:
                matched.Add((itemName, true));
                break;
        }
    }

    private static bool IsSentinel(Expression expression, out string name)
    {
        name = string.Empty;
        return expression is LiteralExpression { Type: LiteralType.String } literal && TryParseSentinel(literal.Value, out name);
    }

    private static int CountSentinels(SqlNode node)
    {
        var counter = new SentinelCounter();
        counter.Visit(node);
        return counter.Count;
    }

    private sealed class SentinelCounter : SqlAstRewriter
    {
        public int Count { get; private set; }

        public override SqlNode Visit(SqlNode node)
        {
            if (node is LiteralExpression { Type: LiteralType.String, Value: string text } && text.Contains(SentinelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Count++;
            }

            return base.Visit(node);
        }
    }
}
