namespace Autheris.Application.Sql.Federation.Pushdown;

using System;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// SQLA-09: resolves a client parameter node of a pushed conjunct to the value the client supplied. A parameter that cannot be resolved by name
/// (positional marker, unknown name, list value, list element slot) yields <c>false</c>: the conjunct is not pushed and DuckDB evaluates it after
/// staging. A NULL placeholder is never bound.
/// </summary>
internal static class PushdownParameterResolver
{
    private const string ParamPrefix = "__param_";

    public static bool TryResolve(SqlNode node, IReadOnlyDictionary<string, object?>? clientValues, out object? value)
    {
        value = null;
        string? name;
        switch (node)
        {
            case ParameterReference { PositionalIndex: null, ListIndex: null } reference:
                name = reference.Name;
                break;
            case NamedBindMarkerExpression marker:
                name = marker.Name;
                break;
            default:
                return false;
        }

        if (clientValues is null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string clean = name.TrimStart('@', ':');
        if (clean.StartsWith(ParamPrefix, StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[ParamPrefix.Length..];
        }

        if (clean.Length == 0 || clean == "?" || !TryGet(clientValues, clean, out var resolved) || resolved is null or ClientListValue)
        {
            return false;
        }

        value = resolved;
        return true;
    }

    private static bool TryGet(IReadOnlyDictionary<string, object?> values, string name, out object? value)
    {
        if (values.TryGetValue(name, out value))
        {
            return true;
        }

        foreach (var (key, candidate) in values)
        {
            if (string.Equals(key.TrimStart('@', ':'), name, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = null;
        return false;
    }
}

/// <summary>SQLA-09: a pushed conjunct references a parameter that cannot be bound from the client values; the conjunct must not be pushed.</summary>
public sealed class PushdownRejectedException : InvalidOperationException
{
    public PushdownRejectedException()
        : base("A pushed predicate references a parameter that has no client value.")
    {
    }
}
