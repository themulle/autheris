using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Autheris.Tests.Parity.Boundary;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Seed;

namespace Autheris.Tests.Parity.Channels;

/// <summary>Renders boundary cases for one channel and parses the response into <see cref="ChannelResult"/> rows of ids (D-8).</summary>
public interface IBoundaryChannel
{
    string Name { get; }

    /// <summary>Null when the channel cannot express the case.</summary>
    RenderedRequest? Render(BoundaryCase c);

    ChannelResult Parse(int status, string body);
}

public static class BoundaryChannels
{
    private static readonly IReadOnlyDictionary<string, IBoundaryChannel> Channels = new Dictionary<string, IBoundaryChannel>
    {
        ["WebSql"] = new WebSqlBoundaryChannel(),
        ["GraphQl"] = new GraphQlBoundaryChannel(),
        ["Rest"] = new RestChannel(),
    };

    public static IBoundaryChannel For(string name) => Channels[name];

    public static async Task<(ChannelResult Result, string? SubmittedSql)> ExecuteAsync(ParityHostFixture host, string channel, BoundaryCase c, CancellationToken ct)
    {
        var ch = For(channel);
        var request = ch.Render(c);
        if (request is null)
        {
            return (ChannelResult.CreateNotExpressible(), null);
        }

        using var client = host.CreateClient();
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.PathAndQuery);
        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType);
        }

        using var response = await client.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return (ch.Parse((int)response.StatusCode, body), request.SubmittedSql);
    }

    internal static IReadOnlyList<int> Ids(ChannelResult r) =>
        r.Rows.Select(row => Convert.ToInt32(row["id"], CultureInfo.InvariantCulture)).ToList();

    internal static string Invariant(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    internal static string Number(object? v) => v switch
    {
        BigInteger b => b.ToString(CultureInfo.InvariantCulture),
        decimal d => Invariant(d),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

public sealed class WebSqlBoundaryChannel : IBoundaryChannel
{
    private const string Select = "SELECT id FROM " + ParitySeed.SalesTable;

    public string Name => "WebSql";

    public ChannelResult Parse(int status, string body) => WebSqlChannel.Parse(status, body);

    public RenderedRequest? Render(BoundaryCase c)
    {
        if (c.Slot == BoundarySlot.Raw)
        {
            return BoundaryRaw.Request(Name, c.RawId!);
        }

        var v = c.Value!;
        object? parameters = null;
        string sql;
        switch (c.Slot)
        {
            case BoundarySlot.Eq:
                sql = $"{Select} WHERE {c.Column!.Name} = {Literal(v.Value)} ORDER BY id";
                break;
            case BoundarySlot.Param:
                sql = $"{Select} WHERE {c.Column!.Name} = @v ORDER BY id";
                parameters = new Dictionary<string, object?> { ["v"] = v.Value };
                break;
            case BoundarySlot.In:
                sql = $"{Select} WHERE {c.Column!.Name} IN ({string.Join(", ", BoundaryOracle.ListFor((ListKind)v.Value!, c.Column).Select(Literal))}) ORDER BY id";
                break;
            case BoundarySlot.Like:
                sql = $"{Select} WHERE {c.Column!.Name} LIKE {Literal(v.Value)} ORDER BY id";
                break;
            case BoundarySlot.Sort:
                sql = $"{Select} ORDER BY \"{((string)v.Value!).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
                break;
            case BoundarySlot.Limit:
                sql = $"{Select} ORDER BY id LIMIT {BoundaryChannels.Number(v.Value)}";
                break;
            case BoundarySlot.Offset:
                sql = $"{Select} ORDER BY id OFFSET {BoundaryChannels.Number(v.Value)}";
                break;
            default:
                return null;
        }

        return new RenderedRequest("POST", "/api/v1/sql", JsonSerializer.Serialize(new { sql, parameters }), SubmittedSql: sql);
    }

    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool b => b ? "TRUE" : "FALSE",
        _ => BoundaryChannels.Number(value),
    };
}

public sealed class GraphQlBoundaryChannel : IBoundaryChannel
{
    private static readonly string Field = GraphQlChannel.FieldName(ParitySeed.SalesTable).Replace("_aggregate", string.Empty, StringComparison.Ordinal);

    public string Name => "GraphQl";

    public ChannelResult Parse(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                string? code = null;
                foreach (var e in errors.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object
                        && ext.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        code = c.GetString();
                        break;
                    }
                }

                return new ChannelResult(status, code, ChannelResult.NoRows, body);
            }

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(Field, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    rows.Add(item.EnumerateObject().ToDictionary(p => p.Name, p => ChannelResult.ToValue(p.Value), StringComparer.OrdinalIgnoreCase));
                }
            }

            return new ChannelResult(status, null, rows, body);
        }
        catch (JsonException)
        {
            return new ChannelResult(status, null, ChannelResult.NoRows, body);
        }
    }

    public RenderedRequest? Render(BoundaryCase c)
    {
        if (c.Slot == BoundarySlot.Raw)
        {
            return BoundaryRaw.Request(Name, c.RawId!);
        }

        var v = c.Value!;
        const string order = "first: 50, orderBy: [{ id: ASC }]";
        Dictionary<string, object?>? variables = null;
        string query;
        switch (c.Slot)
        {
            case BoundarySlot.Eq:
                query = $"{{ {Field}(where: {{ {c.Column!.Name}: {{ eq: {Literal(v.Value)} }} }}, {order}) {{ id }} }}";
                break;
            case BoundarySlot.Param:
                query = $"query($v: {TypeName(c.Column!)}) {{ {Field}(where: {{ {c.Column!.Name}: {{ eq: $v }} }}, {order}) {{ id }} }}";
                variables = new() { ["v"] = v.Value };
                break;
            case BoundarySlot.In:
                query = $"{{ {Field}(where: {{ {c.Column!.Name}: {{ in: [{string.Join(", ", BoundaryOracle.ListFor((ListKind)v.Value!, c.Column).Select(Literal))}] }} }}, {order}) {{ id }} }}";
                break;
            case BoundarySlot.Like:
                query = $"{{ {Field}(where: {{ {c.Column!.Name}: {{ contains: {Literal(v.Value)} }} }}, {order}) {{ id }} }}";
                break;
            case BoundarySlot.Sort:
                query = $"{{ {Field}(first: 50, orderBy: [{{ {(string)v.Value!}: ASC }}]) {{ id }} }}";
                break;
            case BoundarySlot.Limit:
                // A variable page size is costed at the maximum page, so the size is embedded as a literal (bounded cost).
                query = $"{{ {Field}(first: {BoundaryChannels.Number(v.Value)}, orderBy: [{{ id: ASC }}]) {{ id }} }}";
                break;
            case BoundarySlot.Offset:
                query = $"{{ {Field}(offset: {BoundaryChannels.Number(v.Value)}, {order}) {{ id }} }}";
                break;
            default:
                return null;
        }

        return new RenderedRequest("POST", "/graphql", JsonSerializer.Serialize(new { query, variables }));
    }

    private static string TypeName(ParityColumn column) => column.Type switch
    {
        ParityType.Whole => "Int",
        ParityType.Numeric => "Decimal",
        ParityType.Bool => "Boolean",
        _ => "String",
    };

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => JsonSerializer.Serialize(s),
        bool b => b ? "true" : "false",
        _ => BoundaryChannels.Number(value),
    };
}

/// <summary>REST channel (WP2): <c>GET /api/v1/data/default/main/parity_sales</c> with <c>select</c>, <c>filter</c>, <c>orderBy</c>, <c>limit</c>, <c>offset</c>.</summary>
public sealed class RestChannel : IBoundaryChannel
{
    private const string Path = "/api/v1/data/default/main/" + ParitySeed.SalesTable;

    public string Name => "Rest";

    public ChannelResult Parse(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (status == 200 && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                var rows = data.EnumerateArray()
                    .Select(item => (IReadOnlyDictionary<string, object?>)item.EnumerateObject().ToDictionary(p => p.Name, p => ChannelResult.ToValue(p.Value), StringComparer.OrdinalIgnoreCase))
                    .ToList();
                return new ChannelResult(status, null, rows, body);
            }

            string? code = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()
                    : root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            }

            return new ChannelResult(status, code, ChannelResult.NoRows, body);
        }
        catch (JsonException)
        {
            return new ChannelResult(status, null, ChannelResult.NoRows, body);
        }
    }

    public RenderedRequest? Render(BoundaryCase c)
    {
        if (c.Slot == BoundarySlot.Raw)
        {
            return BoundaryRaw.Request(Name, c.RawId!);
        }

        var v = c.Value!;
        var query = c.Slot switch
        {
            BoundarySlot.Eq => $"filter={Enc($"{c.Column!.Name} eq {Literal(v.Value)}")}&orderBy=id&limit=100",
            BoundarySlot.In => $"filter={Enc($"{c.Column!.Name} in ({string.Join(", ", BoundaryOracle.ListFor((ListKind)v.Value!, c.Column).Select(Literal))})")}&orderBy=id&limit=100",
            BoundarySlot.Sort => $"orderBy={Enc((string)v.Value!)}&limit=100",
            BoundarySlot.Limit => $"orderBy=id&limit={Enc(BoundaryChannels.Number(v.Value))}",
            BoundarySlot.Offset => $"orderBy=id&limit=100&offset={Enc(BoundaryChannels.Number(v.Value))}",
            _ => null,
        };
        return query is null ? null : new RenderedRequest("GET", $"{Path}?select=id&{query}");
    }

    private static string Enc(string s) => Uri.EscapeDataString(s);

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool b => b ? "true" : "false",
        _ => BoundaryChannels.Number(value),
    };
}
