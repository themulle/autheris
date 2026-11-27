namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Builds the aggregate and connection parts of the <c>describe_dataset</c> GraphQL block from the schema's root field signatures.
/// Availability is decided from the caller's full visible column list, so a hidden key column is indistinguishable from a missing key.
/// </summary>
public static class McpGraphQlBlock
{
    public const string ReasonNoUsableKey = "no_usable_key";
    public const string ReasonMaskedKey = "masked_key";

    private const int MaxKeyFields = 16;

    /// <summary>The aggregate root field; null when the mapping carries no aggregate signature.</summary>
    public static McpGraphQlAggregate? Aggregate(GraphQlTableMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return mapping.Aggregate is { } a ? new McpGraphQlAggregate(a.Field, a.ResultType, a.Arguments) : null;
    }

    /// <summary>
    /// The keyset connection root field; null when the mapping carries no connection signature. <paramref name="columns"/> is the full
    /// visible list before any narrowing. Unavailable reasons are constant strings and never name a column.
    /// </summary>
    public static McpGraphQlConnection? Connection(GraphQlTableMapping mapping, IReadOnlyList<McpDatasetColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(columns);
        if (mapping.Connection is not { } signature)
        {
            return null;
        }

        McpGraphQlConnection Unavailable(string reason) =>
            new(signature.Field, signature.ResultType, false, reason, null, signature.Arguments);

        if (mapping.KeysetColumns is not { Count: > 0 } keys)
        {
            return Unavailable(ReasonNoUsableKey);
        }

        var byName = new Dictionary<string, McpDatasetColumn>(columns.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            byName.TryAdd(column.Name, column);
        }

        var resolved = new List<McpDatasetColumn>(keys.Count);
        foreach (var key in keys)
        {
            if (!byName.TryGetValue(key, out var column) || column.Access is null || column.GraphQlField is null)
            {
                return Unavailable(ReasonNoUsableKey);
            }

            resolved.Add(column);
        }

        foreach (var column in resolved)
        {
            if (!string.Equals(column.Access, "clear", StringComparison.Ordinal))
            {
                return Unavailable(ReasonMaskedKey);
            }
        }

        IReadOnlyList<string>? keyFields = null;
        if (resolved.Count <= MaxKeyFields)
        {
            var fields = new string[resolved.Count];
            for (var i = 0; i < fields.Length; i++)
            {
                fields[i] = resolved[i].GraphQlField!;
            }

            keyFields = fields;
        }

        return new McpGraphQlConnection(signature.Field, signature.ResultType, true, null, keyFields, signature.Arguments);
    }
}
