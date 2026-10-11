namespace Autheris.Application.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Collects the values of one typed row predicate under deterministic names (<c>__gql_rls_0</c>, <c>__gql_rls_1</c>, ...). A value
/// is only ever reachable as a <see cref="PolicyParameterExpression"/> node plus an entry here (INV-5); no producer renders a value
/// into text. Names are positional per predicate, so two independently built predicates merge only when they use distinct prefixes.
/// </summary>
internal sealed partial class PolicyParameterNamer
{
    internal const string DefaultPrefix = "__gql_rls_";

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,40}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SafePrefixRegex();

    private readonly string _prefix;
    private readonly Dictionary<string, PolicyValue> _values = new(StringComparer.Ordinal);
    private int _next;

    public PolicyParameterNamer(string prefix = DefaultPrefix)
    {
        if (!SafePrefixRegex().IsMatch(prefix))
        {
            throw new ArgumentException("The policy parameter prefix is not a safe identifier prefix.", nameof(prefix));
        }

        _prefix = prefix;
    }

    public IReadOnlyDictionary<string, PolicyValue> Values => _values;

    /// <summary>Registers <paramref name="value"/> and returns its parameter node. <paramref name="isLikePattern"/>: admin-authored wildcards are intentional.</summary>
    public PolicyParameterExpression Add(PolicyValue value, bool isLikePattern = false)
    {
        var name = _prefix + _next++;
        _values[name] = value;
        return new PolicyParameterExpression(name, value.Type, ParameterOrigin.Policy, isLikePattern);
    }

    /// <summary>Converts a consent JSON value to a typed policy value (the legacy extraction rules: raw text for structured values).</summary>
    public static PolicyValue ToPolicyValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => new PolicyValue(element.GetString(), SqlParameterType.String),
        JsonValueKind.Number when element.TryGetInt64(out var l) => new PolicyValue(l, SqlParameterType.Int64),
        JsonValueKind.Number when element.TryGetDecimal(out var d) => new PolicyValue(d, SqlParameterType.Decimal),
        JsonValueKind.Number => new PolicyValue(element.GetDouble(), SqlParameterType.Double),
        JsonValueKind.True => new PolicyValue(true, SqlParameterType.Boolean),
        JsonValueKind.False => new PolicyValue(false, SqlParameterType.Boolean),
        JsonValueKind.Null => new PolicyValue(null, SqlParameterType.Null),
        _ => new PolicyValue(element.GetRawText(), SqlParameterType.String)
    };

    /// <summary>Maps the legacy dialect enum; an unknown value throws (fail closed, no default dialect).</summary>
    public static TargetSqlDialect ToTargetDialect(DatabaseDialect dialect) =>
        dialect == DatabaseDialect.Databricks ? TargetSqlDialect.Databricks : SqlDialectMapper.ToTargetDialect(dialect);

    /// <summary>The dialect IN-list limit, or null when the dialect has none (or no capability entry yet).</summary>
    public static int? InListLimit(TargetSqlDialect dialect)
    {
        try
        {
            return TrinoSqlEngine.Ast.Capabilities.DialectCapabilityTable.Default.Get(dialect).MaxInListItems;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>A quoted identifier column reference, for example <c>"c"."country"</c>; <paramref name="qualified"/> is dot separated.</summary>
    public static ColumnReference Column(string qualified)
    {
        var parts = qualified.Split('.');
        var ids = new SqlIdentifier[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            ids[i] = new SqlIdentifier(parts[i], true);
        }

        return new ColumnReference(new SqlQualifiedName(ids));
    }
}
