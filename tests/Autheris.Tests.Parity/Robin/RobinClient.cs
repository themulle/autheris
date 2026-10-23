using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Autheris.Tests.Parity.Boundary;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Invariant;
using Autheris.Tests.Parity.Seed;
using Shouldly;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Robin;

/// <summary>
/// End-user requests of the Robin persona (block G): WebSQL text with named parameters, GraphQL with variables and REST query strings.
/// Every response passes the no-5xx/no-leak invariant (SEC-PAR-05, SEC-PAR-07) before a scenario looks at the rows, so a server
/// error is a hard failure and never a known gap.
/// </summary>
public sealed class RobinClient(IParityHost host)
{
    public const string SalesRest = "/api/v1/data/default/main/" + ParitySeed.SalesTable;

    private static readonly string[] ValueFields = ["intValue", "decimalValue", "floatValue", "stringValue", "dateTimeValue"];

    public const string AggregateSelection = "alias kind intValue decimalValue floatValue stringValue dateTimeValue";

    public async Task<ChannelResult> WebSqlAsync(string sql, object? parameters = null)
    {
        using var client = host.CreateClient();
        var payload = parameters is null ? (object)new { sql } : new { sql, parameters };
        using var response = await client.PostAsJsonAsync("/api/v1/sql", payload);
        var body = await response.Content.ReadAsStringAsync();
        return Checked(WebSqlChannel.Parse((int)response.StatusCode, body), sql);
    }

    public async Task<ChannelResult> RestAsync(string pathAndQuery)
    {
        using var client = host.CreateClient();
        using var response = await client.GetAsync(pathAndQuery);
        var body = await response.Content.ReadAsStringAsync();
        return Checked(new RestChannel().Parse((int)response.StatusCode, body), null);
    }

    /// <summary>Posts a GraphQL document; rows are read from <c>data.&lt;field&gt;</c> (a list, or an aggregate result with <c>groups</c>).</summary>
    public async Task<ChannelResult> GraphQlAsync(string query, string field, object? variables = null)
    {
        using var client = host.CreateClient();
        var payload = JsonSerializer.Serialize(variables is null ? (object)new { query } : new { query, variables });
        using var response = await client.PostAsync("/graphql", new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        return Checked(ParseGraphQl((int)response.StatusCode, body, field), null);
    }

    /// <summary>Raw HTTP call for endpoints without a dedicated renderer (for example export).</summary>
    public async Task<ChannelResult> PostAsync(string path)
    {
        using var client = host.CreateClient();
        using var response = await client.PostAsync(path, content: null);
        var body = await response.Content.ReadAsStringAsync();
        return Checked(WebSqlChannel.Parse((int)response.StatusCode, body), null);
    }

    // --- WP-R2 ---
    /// <summary>Raw JSON POST through the same authenticated host client as every other helper (SEC-R100-07); response headers are kept for cursor reads.</summary>
    public async Task<RobinHttpResponse> PostJsonAsync(string path, object body)
    {
        using var client = host.CreateClient();
        using var response = await client.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        return new RobinHttpResponse(Checked(WebSqlChannel.Parse((int)response.StatusCode, text), null), headers);
    }

    // --- end WP-R2 ---

    // --- WP-V2b ---
    /// <summary>GET on an OData entity set through the same host client as every other helper; rows come from <c>value</c>, the code from <c>error.code</c>.</summary>
    public async Task<ChannelResult> ODataAsync(string pathAndQuery)
    {
        using var client = host.CreateClient();
        using var response = await client.GetAsync(pathAndQuery);
        var body = await response.Content.ReadAsStringAsync();
        return Checked(ParseOData((int)response.StatusCode, body), null);
    }

    public static ChannelResult ParseOData(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                return new ChannelResult(status, code, ChannelResult.NoRows, body);
            }

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    rows.Add(item.EnumerateObject()
                        .Where(p => !p.Name.StartsWith('@'))
                        .ToDictionary(p => p.Name, p => ChannelResult.ToValue(p.Value), StringComparer.OrdinalIgnoreCase));
                }
            }

            return new ChannelResult(status, null, rows, body);
        }
        catch (JsonException)
        {
            return new ChannelResult(status, null, ChannelResult.NoRows, body);
        }
    }

    // --- WP-V2b unauthenticated ---
    /// <summary>
    /// SEC-R100-12: the one and only client without a test identity. Used by <see cref="AssertUnauthorizedAsync"/> and nowhere else;
    /// never for a positive read or a baseline.
    /// </summary>
    public HttpClient CreateUnauthenticatedClient()
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Test-User-Sid");
        client.DefaultRequestHeaders.Remove("X-Test-Roles");
        client.DefaultRequestHeaders.Remove("X-Test-Tenant");
        return client;
    }

    /// <summary>Sends the request without an identity and asserts exactly HTTP 401 with no seed value in the body (no redirect, no 200).</summary>
    public async Task AssertUnauthorizedAsync(HttpMethod method, string path, string? jsonBody = null)
    {
        using var client = CreateUnauthenticatedClient();
        using var request = new HttpRequestMessage(method, path);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if ((int)response.StatusCode != 401)
        {
            throw new DeniedRequestSucceededException($"{method} {path} without an identity returned HTTP {(int)response.StatusCode}, expected 401.");
        }

        foreach (var marker in new[] { "North", "South", "Widget", "Gadget", "Gizmo", "under_score", "135.75", "\"rows\"" })
        {
            if (body.Contains(marker, StringComparison.Ordinal))
            {
                throw new DeniedRequestSucceededException($"{method} {path} without an identity leaks a seed value in the 401 body.");
            }
        }
    }

    // --- end WP-V2b unauthenticated ---
    // --- end WP-V2b ---

    private static ChannelResult Checked(ChannelResult r, string? submittedSql)
    {
        ParityAssert.NoServerError(r, submittedSql);
        return r;
    }

    public static ChannelResult ParseGraphQl(int status, string body, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
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

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            var data = root.GetProperty("data").GetProperty(field);
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    rows.Add(item.EnumerateObject().ToDictionary(p => p.Name, p => ChannelResult.ToValue(p.Value), StringComparer.OrdinalIgnoreCase));
                }
            }
            else if (data.TryGetProperty("groups", out var groups))
            {
                foreach (var group in groups.EnumerateArray())
                {
                    var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    if (group.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in key.EnumerateObject())
                        {
                            map[p.Name] = ChannelResult.ToValue(p.Value);
                        }
                    }

                    if (group.TryGetProperty("rowCount", out var rc))
                    {
                        map["rowCount"] = ChannelResult.ToValue(rc);
                    }

                    foreach (var agg in group.GetProperty("aggregates").EnumerateArray())
                    {
                        map[agg.GetProperty("alias").GetString()!] = ValueOf(agg);
                    }

                    rows.Add(map);
                }
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
        foreach (var name in ValueFields)
        {
            if (agg.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null)
            {
                return ChannelResult.ToValue(v);
            }
        }

        return null;
    }

    /// <summary><c>extensions.pagination.isTruncated</c> of a GraphQL body (#39).</summary>
    public static bool IsTruncatedSignal(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("extensions", out var ext)
                   && ext.TryGetProperty("pagination", out var page)
                   && page.TryGetProperty("isTruncated", out var t)
                   && t.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Assertions on end-user results: the rows must equal the hand-computed expectation, not merely avoid a 5xx.</summary>
public static class RobinAssert
{
    public static ChannelResult Ok(this ChannelResult r)
    {
        if (r.Status != 200 || ParityAssert.IsError(r))
        {
            throw new XunitException($"Expected success, got HTTP {r.Status} code '{r.Code}': {Snippet(r.RawBody)}");
        }

        return r;
    }

    /// <summary>A rejection must be a 4xx with a stable code from the public catalogue.</summary>
    public static ChannelResult Rejected(this ChannelResult r, string? expectedCode = null)
    {
        if (!ParityAssert.IsError(r))
        {
            throw new XunitException($"Expected a rejection, got HTTP {r.Status}: {Snippet(r.RawBody)}");
        }

        ParityAssert.StableCode(r);
        if (expectedCode is not null && !string.Equals(r.Code, expectedCode, StringComparison.Ordinal))
        {
            throw new XunitException($"Expected code '{expectedCode}', got HTTP {r.Status} code '{r.Code}': {Snippet(r.RawBody)}");
        }

        return r;
    }

    /// <summary>Success with the given columns equal to the expected rows (order-insensitive unless <paramref name="ordered"/>).</summary>
    public static void RowsShouldBe(this ChannelResult r, string[] columns, object?[][] expected, bool ordered = false, bool prefixOnly = false)
    {
        r.Ok();
        var actual = r.Rows.Select(row => columns.Select(c => Cell(row, c)).ToArray()).ToList();
        var exp = expected.Select(e => e.Select(Normalize).ToArray()).ToList();
        if (!ordered)
        {
            actual = actual.OrderBy(Key, StringComparer.Ordinal).ToList();
            exp = exp.OrderBy(Key, StringComparer.Ordinal).ToList();
        }

        var message = $"rows differ (columns {string.Join(",", columns)}): {Snippet(r.RawBody)}";
        if (prefixOnly)
        {
            actual = actual.Take(exp.Count).ToList();
        }

        actual.Count.ShouldBe(exp.Count, message);
        for (var i = 0; i < exp.Count; i++)
        {
            for (var j = 0; j < columns.Length; j++)
            {
                Same(actual[i][j], exp[i][j]).ShouldBeTrue($"{message}; row {i} column {columns[j]}: '{actual[i][j]}' vs '{exp[i][j]}'");
            }
        }
    }

    public static void IdsShouldBe(this ChannelResult r, params int[] ids) =>
        r.RowsShouldBe(["id"], ids.Select(i => new object?[] { i }).ToArray());

    public static void IdsShouldBeInOrder(this ChannelResult r, params int[] ids) =>
        r.RowsShouldBe(["id"], ids.Select(i => new object?[] { i }).ToArray(), ordered: true);

    private static string Key(string?[] row) => string.Join("\u001f", row.Select(c => c ?? "\u007f"));

    private static string? Cell(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) ? Normalize(v) : throw new XunitException($"Column '{column}' missing in row [{string.Join(",", row.Keys)}].");

    private static string? Normalize(object? v) => v switch
    {
        null => null,
        bool b => b ? "1" : "0",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        long or int => Convert.ToString(v, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    private static bool Same(string? actual, string? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (decimal.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var e))
        {
            return Math.Abs(a - e) < 0.000001m;
        }

        // A date part ("2024", "2024-03", "2024-03-31") matches the start of a timestamp text.
        if (System.Text.RegularExpressions.Regex.IsMatch(expected, @"^\d{4}(-\d{2}(-\d{2})?)?$"))
        {
            return actual.StartsWith(expected, StringComparison.Ordinal);
        }

        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    /// <summary>The calendar day (first ten characters) of an ISO date or timestamp value.</summary>
    public static string Day(object? value) => (value?.ToString() ?? string.Empty)[..Math.Min(10, (value?.ToString() ?? string.Empty).Length)];

    public static string Snippet(string body) => body.Length <= 300 ? body : body[..300] + "...";
}

/// <summary>Status, body (inside <see cref="Result"/>) and response headers of a raw JSON POST.</summary>
public sealed record RobinHttpResponse(ChannelResult Result, IReadOnlyDictionary<string, string> Headers)
{
    public int Status => Result.Status;

    public string Body => Result.RawBody;
}
