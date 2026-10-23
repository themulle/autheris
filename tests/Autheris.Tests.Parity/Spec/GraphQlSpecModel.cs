using System.Text;
using System.Text.Json;

namespace Autheris.Tests.Parity.Spec;

/// <summary>A GraphQL type reference of an introspection result.</summary>
public sealed record GqlTypeRef(string Kind, string? Name, GqlTypeRef? OfType)
{
    public bool IsNonNull => Kind == "NON_NULL";

    public string NamedType => Name ?? OfType!.NamedType;

    public static GqlTypeRef From(JsonElement e) => new(
        e.GetProperty("kind").GetString()!,
        e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
        e.TryGetProperty("ofType", out var o) && o.ValueKind == JsonValueKind.Object ? From(o) : null);
}

public sealed record GqlInput(string Name, GqlTypeRef Type, bool HasDefault);

public sealed record GqlField(string Name, GqlTypeRef Type, IReadOnlyList<GqlInput> Args);

public sealed record GqlType(string Kind, string Name, IReadOnlyList<GqlField> Fields, IReadOnlyList<GqlInput> InputFields, IReadOnlyList<string> EnumValues);

/// <summary>Parsed introspection result plus the type-driven synthesis of one valid minimal literal per argument (D-12).</summary>
public sealed class GraphQlSpecModel
{
    public const string IntrospectionQuery =
        "query Spec { __schema { queryType { name } mutationType { name } subscriptionType { name } types { kind name " +
        "fields { name args { name defaultValue type { ...T } } type { ...T } } " +
        "inputFields { name defaultValue type { ...T } } enumValues { name } } } } " +
        "fragment T on __Type { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } } } }";

    private const int MaxDepth = 4;

    private readonly Dictionary<string, GqlType> _types;

    private GraphQlSpecModel(Dictionary<string, GqlType> types, string? query, string? mutation, string? subscription)
    {
        _types = types;
        QueryRoot = query;
        MutationRoot = mutation;
        SubscriptionRoot = subscription;
    }

    public string? QueryRoot { get; }

    public string? MutationRoot { get; }

    public string? SubscriptionRoot { get; }

    public IReadOnlyList<GqlField> RootFields(string? root) => root is not null && _types.TryGetValue(root, out var t) ? t.Fields : [];

    public GqlType? Type(string name) => _types.GetValueOrDefault(name);

    public static GraphQlSpecModel Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var schema = doc.RootElement.GetProperty("data").GetProperty("__schema");
        var types = new Dictionary<string, GqlType>(StringComparer.Ordinal);
        foreach (var t in schema.GetProperty("types").EnumerateArray())
        {
            var name = t.GetProperty("name").GetString()!;
            types[name] = new GqlType(
                t.GetProperty("kind").GetString()!,
                name,
                Fields(t, "fields", f => new GqlField(f.GetProperty("name").GetString()!, GqlTypeRef.From(f.GetProperty("type")), Inputs(f, "args"))),
                Inputs(t, "inputFields"),
                Fields(t, "enumValues", e => e.GetProperty("name").GetString()!));
        }

        return new GraphQlSpecModel(types, RootName(schema, "queryType"), RootName(schema, "mutationType"), RootName(schema, "subscriptionType"));
    }

    /// <summary>The selection set that makes a field valid: the first leaf field of a composite result, nothing for scalars and enums.</summary>
    public string Selection(GqlTypeRef type)
    {
        var named = _types.GetValueOrDefault(type.NamedType);
        if (named is not { Kind: "OBJECT" or "INTERFACE" or "UNION" })
        {
            return string.Empty;
        }

        var leaf = named.Fields.FirstOrDefault(f => _types.GetValueOrDefault(f.Type.NamedType) is not { Kind: "OBJECT" or "INTERFACE" or "UNION" });
        return leaf is null ? " { __typename }" : $" {{ {leaf.Name} }}";
    }

    /// <summary>A valid minimal literal for <paramref name="type"/>; null when none can be built (for example a required enum without values).</summary>
    public string? Literal(GqlTypeRef type, int depth = 0, string? parent = null)
    {
        switch (type.Kind)
        {
            case "NON_NULL":
                return Literal(type.OfType!, depth, parent);
            case "LIST":
                var item = Literal(type.OfType!, depth + 1, parent);
                return item is null ? null : $"[{item}]";
            case "ENUM":
                var values = _types.GetValueOrDefault(type.Name!)?.EnumValues;
                if (values is not { Count: > 0 })
                {
                    return null;
                }

                // a time bucket needs a temporal column
                // (a table without a temporal column cannot express a time bucket)
                return parent is not null && parent.Contains("time_bucket", StringComparison.Ordinal)
                    ? values.FirstOrDefault(v => v.EndsWith("date", StringComparison.Ordinal) || v.EndsWith("_at", StringComparison.Ordinal))
                    : values[0];
            case "INPUT_OBJECT":
                return Object(type.Name!, depth);
            default:
                return type.Name switch
                {
                    "Int" => "1",
                    "Float" => "1.5",
                    "Boolean" => "true",
                    "ID" => "\"1\"",
                    "DateTime" or "DateTimeOffset" => "\"2024-01-10T12:00:00Z\"",
                    "Date" => "\"2024-01-10\"",
                    "Decimal" => "1",
                    "Long" or "BigInt" => "1",
                    _ => "\"x\"",
                };
        }
    }

    private string? Object(string name, int depth)
    {
        if (!_types.TryGetValue(name, out var t) || depth > MaxDepth)
        {
            return "{}";
        }

        var chosen = t.InputFields.Where(f => f.Type.IsNonNull && !f.HasDefault).ToList();
        if (t.InputFields.Any(f => f.Name == "direction") && t.InputFields.FirstOrDefault(f => f.Name == "column") is { } column && !chosen.Contains(column))
        {
            chosen.Add(column); // an order entry names exactly one sort key
        }

        if (chosen.Count == 0)
        {
            // an all-optional input (filter, order) is minimal with exactly one entry, never a boolean combinator or itself
            var first = t.InputFields.FirstOrDefault(f => f.Name is not ("and" or "or" or "not") && f.Type.NamedType != name);
            if (first is not null)
            {
                chosen.Add(first);
            }
        }

        var parts = new List<string>();
        foreach (var f in chosen)
        {
            var lit = Literal(f.Type, depth + 1, f.Name == "column" ? name : null);
            if (lit is null)
            {
                return null; // an entry whose required value cannot be built makes the whole argument inexpressible
            }

            parts.Add($"{f.Name}: {lit}");
        }

        return "{" + string.Join(", ", parts) + "}";
    }

    private static string? RootName(JsonElement schema, string key) =>
        schema.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Object ? e.GetProperty("name").GetString() : null;

    private static IReadOnlyList<T> Fields<T>(JsonElement t, string key, Func<JsonElement, T> map) =>
        t.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array ? [.. arr.EnumerateArray().Select(map)] : [];

    private static IReadOnlyList<GqlInput> Inputs(JsonElement t, string key) =>
        Fields(t, key, a => new GqlInput(
            a.GetProperty("name").GetString()!,
            GqlTypeRef.From(a.GetProperty("type")),
            a.TryGetProperty("defaultValue", out var d) && d.ValueKind == JsonValueKind.String));

    /// <summary>One call: the field with its required arguments plus <paramref name="extra"/> (null = required only); null when it cannot be built.</summary>
    public string? Build(GqlField field, GqlInput? extra)
    {
        var args = new List<string>();
        // list fields are rejected without a page size (QUERY_TOO_COMPLEX), so 'first' belongs to every minimal call
        foreach (var a in field.Args.Where(a => ((a.Type.IsNonNull && !a.HasDefault) || a.Name == "first") && a.Name != extra?.Name))
        {
            var lit = Literal(a.Type);
            if (lit is null)
            {
                return null;
            }

            args.Add($"{a.Name}: {lit}");
        }

        // an aggregation orders by a grouped column or an alias, so the ordered call also groups by that column
        if (extra?.Name == "orderBy" && field.Args.FirstOrDefault(a => a.Name == "groupBy") is { } groupBy)
        {
            var group = Literal(groupBy.Type);
            if (group is not null)
            {
                args.Add($"groupBy: {group}");
            }
        }

        if (extra is not null)
        {
            var lit = Literal(extra.Type);
            if (lit is null)
            {
                return null;
            }

            args.Add($"{extra.Name}: {lit}");
        }

        var sb = new StringBuilder("{ ").Append(field.Name);
        if (args.Count > 0)
        {
            sb.Append('(').Append(string.Join(", ", args)).Append(')');
        }

        return sb.Append(Selection(field.Type)).Append(" }").ToString();
    }
}
