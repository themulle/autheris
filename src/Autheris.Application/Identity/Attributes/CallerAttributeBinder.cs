namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Globalization;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
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

    public static PolicyPredicate Bind(PolicyPredicate predicate, CallerAttributeSet set, CallerAttributePadding padding)
        => throw new NotImplementedException("WP-4");
}
