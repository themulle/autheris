using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Autheris.Tests.Parity.Infrastructure;

namespace Autheris.Tests.Parity.Channels;

/// <summary>Renders the logical query as SQL text and posts it to <c>/api/v1/sql</c> (D-8). Code = <c>code ?? error</c>.</summary>
public sealed class WebSqlChannel(ParityHostFixture host) : IParityChannel
{
    public string Name => "WebSql";

    public static string Render(ParityQuery q)
    {
        if (q.RawSql is not null)
        {
            return q.RawSql;
        }

        var select = new List<string>();
        select.AddRange(q.GroupBy ?? []);
        foreach (var a in q.Aggregates)
        {
            var arg = a.Column ?? "*";
            var expr = a.Fn switch
            {
                ParityFn.Count => $"COUNT({arg})",
                ParityFn.Sum => $"SUM({arg})",
                ParityFn.Avg => $"AVG({arg})",
                ParityFn.Min => $"MIN({arg})",
                ParityFn.Max => $"MAX({arg})",
                _ => $"COUNT(DISTINCT {arg})",
            };
            select.Add($"{expr} AS {a.Alias}");
        }

        var sb = new StringBuilder($"SELECT {string.Join(", ", select)} FROM {q.Table}");
        if (q.Where is { Count: > 0 })
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", q.Where.Select(w => $"{w.Column} = '{w.Value.Replace("'", "''", StringComparison.Ordinal)}'")));
        }

        if (q.GroupBy is { Count: > 0 })
        {
            sb.Append(" GROUP BY ").Append(string.Join(", ", q.GroupBy));
            sb.Append(" ORDER BY ").Append(string.Join(", ", q.GroupBy));
        }

        return sb.ToString();
    }

    public async Task<ChannelResult> ExecuteAsync(ParityQuery query, CancellationToken ct)
    {
        using var client = host.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/sql", new { sql = Render(query) }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return Parse((int)response.StatusCode, body);
    }

    /// <summary>Parses a WebSQL response body (200 rows or an error envelope; code = <c>code ?? error</c>).</summary>
    public static ChannelResult Parse(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (status == 200 && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("columns", out var cols))
            {
                var names = cols.EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToArray();
                var rows = new List<IReadOnlyDictionary<string, object?>>();
                foreach (var row in root.GetProperty("rows").EnumerateArray())
                {
                    var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    if (row.ValueKind == JsonValueKind.Array)
                    {
                        var i = 0;
                        foreach (var cell in row.EnumerateArray())
                        {
                            map[names[i++]] = ChannelResult.ToValue(cell);
                        }
                    }
                    else
                    {
                        foreach (var p in row.EnumerateObject())
                        {
                            map[p.Name] = ChannelResult.ToValue(p.Value);
                        }
                    }

                    rows.Add(map);
                }

                return new ChannelResult(status, null, rows, body);
            }

            string? code = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // O-5b step 2: every WebSQL error is a governed problem body with a string `code`; the bare `error` name is gone.
                code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            }

            return new ChannelResult(status, code, ChannelResult.NoRows, body);
        }
        catch (JsonException)
        {
            return new ChannelResult(status, null, ChannelResult.NoRows, body);
        }
    }
}
