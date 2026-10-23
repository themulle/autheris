using System.Text;
using System.Text.Json;
using Autheris.Tests.Parity.Infrastructure;

namespace Autheris.Tests.Parity.Channels;

/// <summary>Renders the typed <c>_aggregate</c> field and posts it to <c>/graphql</c> (D-8). Code = first <c>errors[].extensions.code</c>.</summary>
public sealed class GraphQlChannel(ParityHostFixture host) : IParityChannel
{
    private const string ValueSelection = "alias kind intValue decimalValue floatValue stringValue dateTimeValue";

    public string Name => "GraphQl";

    public static string? Render(ParityQuery q)
    {
        if (q.RawSql is not null)
        {
            return null;
        }

        var args = new List<string>();
        var groupBy = q.GroupBy ?? [];
        if (groupBy.Count > 0)
        {
            args.Add($"groupBy: [{string.Join(", ", groupBy)}]");
        }

        args.Add("aggregates: [" + string.Join(", ", q.Aggregates.Select(a =>
        {
            var fn = a.Fn switch
            {
                ParityFn.CountDistinct => "COUNT_DISTINCT",
                _ => a.Fn.ToString().ToUpperInvariant(),
            };
            var col = a.Column is null ? string.Empty : $", column: {a.Column}";
            return $"{{ fn: {fn}{col}, alias: \"{a.Alias}\" }}";
        })) + "]");
        if (q.Where is { Count: > 0 })
        {
            args.Add("where: { " + string.Join(", ", q.Where.Select(w => $"{w.Column}: {{ eq: {JsonSerializer.Serialize(w.Value)} }}")) + " }");
        }

        if (groupBy.Count > 0)
        {
            args.Add($"orderBy: [{string.Join(", ", groupBy.Select(g => $"{{ column: {g} }}"))}]");
        }

        var key = groupBy.Count > 0 ? $"key {{ {string.Join(" ", groupBy)} }} " : string.Empty;
        return $"{{ {FieldName(q.Table)}({string.Join(", ", args)}) {{ groups {{ {key}aggregates {{ {ValueSelection} }} }} }} }}";
    }

    public static string FieldName(string table) => $"default_main_{table}_aggregate";

    public async Task<ChannelResult> ExecuteAsync(ParityQuery query, CancellationToken ct)
    {
        var graphql = Render(query);
        if (graphql is null)
        {
            return ChannelResult.CreateNotExpressible();
        }

        using var client = host.CreateClient();
        var payload = JsonSerializer.Serialize(new { query = graphql });
        using var response = await client.PostAsync("/graphql", new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                string? code = null;
                foreach (var e in errors.EnumerateArray())
                {
                    if (e.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        code = c.GetString();
                        break;
                    }
                }

                return new ChannelResult(status, code, ChannelResult.NoRows, body);
            }

            var field = FieldName(query.Table);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var group in root.GetProperty("data").GetProperty(field).GetProperty("groups").EnumerateArray())
            {
                var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (group.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in key.EnumerateObject())
                    {
                        map[p.Name] = ChannelResult.ToValue(p.Value);
                    }
                }

                foreach (var agg in group.GetProperty("aggregates").EnumerateArray())
                {
                    map[agg.GetProperty("alias").GetString()!] = ValueOf(agg);
                }

                rows.Add(map);
            }

            return new ChannelResult(status, null, rows, body);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new ChannelResult(status, null, ChannelResult.NoRows, body);
        }
    }

    private static object? ValueOf(JsonElement agg)
    {
        foreach (var name in new[] { "intValue", "decimalValue", "floatValue", "stringValue", "dateTimeValue" })
        {
            if (agg.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null)
            {
                return ChannelResult.ToValue(v);
            }
        }

        return null;
    }
}
