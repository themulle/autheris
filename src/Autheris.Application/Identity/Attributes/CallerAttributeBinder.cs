namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// Reserved names of caller attribute parameters (BC-129-16): <c>__gql_caller_&lt;attribute&gt;__&lt;index&gt;</c>. The attribute name never
/// contains <c>__</c> (see <see cref="CallerAttributeDefinition.NameGrammar"/>), so the name is reversible. Client parameters spelled like
/// this are refused at every binder (case-insensitive).
/// </summary>
public static class CallerAttributeParameterName
{
    public const string Prefix = "__gql_caller_";

    public static string Format(string attribute, int index) => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{attribute}__{index}");

    public static bool IsReserved(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string name, out string attribute)
    {
        attribute = string.Empty;
        if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = name.AsSpan(Prefix.Length);
        int separator = rest.LastIndexOf("__", StringComparison.Ordinal);
        if (separator <= 0 || !int.TryParse(rest[(separator + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        var candidate = rest[..separator].ToString();
        if (!CallerAttributeDefinition.IsValidName(candidate))
        {
            return false;
        }

        attribute = candidate;
        return true;
    }
}

/// <summary>Padding of expanded lists (A-6): the length is <c>min(nextPow2(count), MaxValues)</c>, the last value repeated (neutral for IN and NOT IN).</summary>
public sealed record CallerAttributePadding(bool PadToBuckets, int MaxValues)
{
    public static CallerAttributePadding Default { get; } = new(true, 256);

    public int PaddedCount(int count)
    {
        if (!PadToBuckets || count <= 1)
        {
            return count;
        }

        long bucket = 1;
        while (bucket < count)
        {
            bucket <<= 1;
        }

        return (int)Math.Max(count, Math.Min(bucket, MaxValues));
    }
}

/// <summary>
/// The only code that creates a valued <c>__gql_caller_</c> parameter (BC-129-16, ST-129-14). Values enter the typed policy as bound
/// policy parameters of origin <see cref="ParameterOrigin.Policy"/> and never as text.
/// </summary>
public static class CallerAttributeBinder
{
    /// <summary>The padded parameters of a resolved value, registered in <paramref name="namer"/> with their values.</summary>
    internal static IReadOnlyList<PolicyParameterExpression> Expand(PolicyParameterNamer namer, string attribute, CallerAttributeValue value, CallerAttributePadding padding)
    {
        ArgumentNullException.ThrowIfNull(namer);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(padding);
        if (value.Count == 0)
        {
            throw new InvalidOperationException("An empty attribute value is never expanded.");
        }

        int padded = padding.PaddedCount(value.Count);
        var parameters = new List<PolicyParameterExpression>(padded);
        for (int i = 0; i < padded; i++)
        {
            object element = value[Math.Min(i, value.Count - 1)];
            parameters.Add(namer.AddCallerAttribute(CallerAttributeParameterName.Format(attribute, i), new PolicyValue(element, value.ElementType)));
        }

        return parameters;
    }

    /// <summary>An unbound slot of a Virtual Filter predicate: a policy parameter with the reserved name and no value (<see cref="SqlParameterType.Null"/>).</summary>
    internal static PolicyParameterExpression CreateSlot(PolicyParameterNamer namer, string attribute) =>
        namer.AddCallerAttribute(CallerAttributeParameterName.Format(attribute, 0), new PolicyValue(null, SqlParameterType.Null));

    /// <summary>
    /// The binding pass of a Virtual Filter predicate (A-4): every slot becomes a bound parameter holding the resolved value (a comparison: one
    /// value; the sole item of IN or NOT IN: the padded list). If <em>anything</em> is unresolved, over cardinality, of the wrong cardinality or
    /// throws, the whole predicate is <see cref="PolicyPredicate.DenyAll"/> (BC-129-06): a Virtual Filter is a restriction, never a grant.
    /// </summary>
    public static PolicyPredicate Bind(PolicyPredicate predicate, CallerAttributeSet set, CallerAttributePadding padding)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        try
        {
            return BindCore(predicate, set, padding);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PolicyPredicate.DenyAll;
        }
    }

    /// <summary>The attribute names of the unbound slots of <paramref name="predicate"/>.</summary>
    public static IReadOnlyList<string> SlotAttributes(PolicyPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (name, value) in predicate.Parameters)
        {
            if (value.Type == SqlParameterType.Null && CallerAttributeParameterName.TryParse(name, out var attribute))
            {
                names.Add(attribute);
            }
        }

        return [.. names];
    }

    private static PolicyPredicate BindCore(PolicyPredicate predicate, CallerAttributeSet set, CallerAttributePadding padding)
    {
        var slots = predicate.Parameters
            .Where(p => p.Value.Type == SqlParameterType.Null && CallerAttributeParameterName.TryParse(p.Key, out _))
            .Select(p => p.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (slots.Count == 0)
        {
            return predicate;
        }

        var namer = new PolicyParameterNamer();
        var rewritten = (Expression)new SlotBinder(slots, set, padding, namer).Visit(predicate.Expression);
        var values = predicate.Parameters.Where(p => !slots.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var (name, value) in namer.Values)
        {
            values[name] = value;
        }

        return PolicyPredicate.Create(rewritten, values);
    }

    private sealed class SlotBinder(HashSet<string> slots, CallerAttributeSet set, CallerAttributePadding padding, PolicyParameterNamer namer) : SqlAstRewriter
    {
        public override SqlNode Visit(SqlNode node)
        {
            switch (node)
            {
                case InListExpression inList when inList.Items.Any(IsSlot):
                {
                    if (inList.Items.Count != 1)
                    {
                        throw new InvalidOperationException("A caller slot must be the only item of an IN list.");
                    }

                    var slot = (PolicyParameterExpression)inList.Items[0];
                    var value = Resolved(slot);
                    var items = Expand(namer, AttributeOf(slot), value, padding).Cast<Expression>().ToList();
                    return inList with { Operand = (Expression)Visit(inList.Operand), Items = items };
                }

                case BinaryExpression binary when IsComparison(binary.Operator) && (IsSlot(binary.Left) || IsSlot(binary.Right)):
                {
                    if (IsSlot(binary.Left) && IsSlot(binary.Right))
                    {
                        throw new InvalidOperationException("A comparison of two caller slots is not allowed.");
                    }

                    Expression Single(Expression side)
                    {
                        if (!IsSlot(side))
                        {
                            return (Expression)Visit(side);
                        }

                        var slot = (PolicyParameterExpression)side;
                        var value = Resolved(slot);
                        if (value.Count != 1)
                        {
                            throw new InvalidOperationException("A comparison needs a single attribute value.");
                        }

                        return Expand(namer, AttributeOf(slot), value, new CallerAttributePadding(false, 1))[0];
                    }

                    return binary with { Left = Single(binary.Left), Right = Single(binary.Right) };
                }

                case PolicyParameterExpression slot when IsSlot(slot):
                    throw new InvalidOperationException("A caller slot sits in a position that cannot be bound.");
            }

            return base.Visit(node);
        }

        private static bool IsComparison(BinaryOperator op) => op is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan
            or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual;

        private bool IsSlot(Expression expression) => expression is PolicyParameterExpression parameter && slots.Contains(parameter.Name);

        private static string AttributeOf(PolicyParameterExpression slot) =>
            CallerAttributeParameterName.TryParse(slot.Name, out var attribute) ? attribute : throw new InvalidOperationException("Not a caller slot.");

        private CallerAttributeValue Resolved(PolicyParameterExpression slot)
        {
            var attribute = AttributeOf(slot);
            if (!set.TryGet(attribute, out var result) || result.Outcome != CallerAttributeOutcome.Resolved || result.Value is not { Count: > 0 } value)
            {
                throw new InvalidOperationException("A caller attribute is not resolved.");
            }

            return value;
        }
    }
}
