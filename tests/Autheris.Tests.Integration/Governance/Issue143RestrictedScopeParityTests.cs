namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// REQ-GOV-143 (PLAN-GOV-143 AC-7, T-GOV143-17): a catalog-sensitive column with a base <c>Clear</c> is in clear text for an unrestricted token
/// and never for a holder of a restricted scope (<c>Agent.Read</c>), in every transport. Real consents (no consent bypass), real SQLite data.
/// The unrestricted caller is the positive control: a channel that does not return the clear values there proves nothing for the restricted one.
/// Run: dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~Issue143RestrictedScopeParityTests"
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue143RestrictedScopeParityTests : IClassFixture<Issue143RestrictedScopeParityTests.SensitiveFixture>
{
    private const string RestrictedScope = "Agent.Read";
    private static readonly TableIdentifier Air1Id = new("default", "main", "air1");

    /// <summary>The clear <c>air1.value</c> of the rows the David consent filter releases (ids 5 to 10).</summary>
    private static readonly double[] ClearValues = [3.0, 3.5, 4.0, 4.5, 5.0, 5.5];

    private readonly SensitiveFixture _fixture;

    public Issue143RestrictedScopeParityTests(SensitiveFixture fixture) => _fixture = fixture;

    /// <summary><c>air1.value</c> is catalog-sensitive; the consent grants it a base <c>Clear</c> (all columns), without a scope-specific rule.</summary>
    public sealed class SensitiveFixture : RowFilterChannelParityTests.Fixture
    {
        protected override void ConfigureHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            // One caller sends four requests per channel (and GraphQL costs by complexity): keep the per-SID budget out of the comparison.
            builder.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokenBucketCapacity", "100000");
            builder.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokensPerSecond", "10000");
        }

        protected override async Task SeedExtraGovernanceAsync(IGovernanceRepository repo)
        {
            var air1 = (await repo.GetTableMetadataAsync(Air1Id))!;
            await repo.UpsertTableMetadataAsync(air1 with
            {
                Columns = air1.Columns
                    .Select(c => c.ColumnName == "value"
                        ? new TableColumn { Id = c.Id, TableId = c.TableId, ColumnName = c.ColumnName, DataType = c.DataType, IsSensitive = true, Meta = c.Meta }
                        : c)
                    .ToList()
            });
        }
    }

    public static IEnumerable<object[]> Channels() => new[]
    {
        "websql", "trino", "odata", "graphql", "mcp-query-graphql", "arrow-export", "flight-sql", "olap"
    }.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Channel_UnrestrictedToken_ReceivesClearValues_RestrictedToken_NeverDoes(string channel)
    {
        var unrestricted = await ValuesAsync(channel, scope: null);
        var other = await ValuesAsync(channel, scope: "some.other.scope");
        var restricted = await ValuesAsync(channel, scope: RestrictedScope);
        var prefixed = await ValuesAsync(channel, scope: "api://app/" + RestrictedScope);

        // Positive control: the channel does deliver the clear values to a caller without a restricted scope.
        unrestricted.ShouldBe(ClearValues, ignoreOrder: true, customMessage: $"unrestricted, channel '{channel}'");
        other.ShouldBe(ClearValues, ignoreOrder: true, customMessage: $"other scope, channel '{channel}'");

        // The restricted holder gets masked values, nothing, or an error, but never a clear value.
        restricted.ShouldNotContain(v => ClearValues.Contains(v), $"restricted, channel '{channel}'");
        prefixed.ShouldNotContain(v => ClearValues.Contains(v), $"prefixed restricted scope, channel '{channel}'");
    }

    private async Task<IReadOnlyList<double>> ValuesAsync(string channel, string? scope)
    {
        _fixture.CallerSid = RowFilterChannelParityTests.Fixture.DavidSid;
        _fixture.ExtraHeaders.Clear();
        _fixture.ExtraHeaders["X-Test-Tier"] = "Internal"; // GraphQL cost quota: keep the per-tier budget out of the comparison
        if (scope != null)
        {
            _fixture.ExtraHeaders["X-Test-Scope"] = scope;
        }

        try
        {
            return await Channels(channel)(_fixture);
        }
        catch (Exception ex) when (ex is ShouldAssertException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A masked REAL column may be refused by a typed channel (for example a Float field): no value left the gateway.
            if (scope == null || scope.StartsWith("some", StringComparison.Ordinal))
            {
                throw;
            }

            return [];
        }
        finally
        {
            _fixture.ExtraHeaders.Clear();
        }
    }

    private static Func<RowFilterChannelParityTests.Fixture, Task<IReadOnlyList<double>>> Channels(string channel) => channel switch
    {
        "websql" => async f =>
        {
            var r = await f.Client().PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT value FROM default.main.air1" });
            using var doc = await JsonOrFailAsync(r);
            return Numbers(doc.RootElement.GetProperty("rows"), doc.RootElement.GetProperty("columns"));
        },
        "trino" => async f =>
        {
            var c = f.Client();
            c.DefaultRequestHeaders.Add("X-Trino-User", "test-user");
            c.DefaultRequestHeaders.Add("X-Trino-Wait-Timeout", "30s");
            var r = await c.PostAsync("/v1/statement", new StringContent("SELECT value FROM default.main.air1", Encoding.UTF8, "text/plain"));
            using var doc = await JsonOrFailAsync(r);
            var root = doc.RootElement;
            root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FINISHED", root.ToString());
            return root.TryGetProperty("data", out var data)
                ? data.EnumerateArray().Select(row => Number(row[0])).OfType<double>().ToList()
                : [];
        },
        "odata" => async f =>
        {
            var r = await f.Client().GetAsync("/odata/v4/default/main/air1?$select=value&$top=1000");
            using var doc = await JsonOrFailAsync(r);
            return Numbers(doc.RootElement.GetProperty("value"), columns: null);
        },
        "graphql" => async f =>
        {
            using var doc = await f.GraphQlAsync("{ default_main_air1(first: 20) { value } }");
            return Numbers(doc.RootElement.GetProperty("data").GetProperty("default_main_air1"), columns: null);
        },
        "mcp-query-graphql" => async f =>
        {
            await using var mcp = await McpTestClient.ConnectAsync(f.Client());
            var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_graphql", new { query = "{ default_main_air1(first: 20) { value } }" });
            isError.ShouldBeFalse(payload.ToString());
            return Numbers(payload.GetProperty("data").GetProperty("default_main_air1"), columns: null);
        },
        "arrow-export" => async f =>
        {
            var r = await f.Client().PostAsync("/api/v1/export/arrow?table=default.main.air1", content: null);
            if (r.StatusCode != HttpStatusCode.OK)
            {
                throw new ShouldAssertException($"HTTP {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
            }

            return await ArrowValuesAsync(await r.Content.ReadAsStreamAsync());
        },
        "flight-sql" => async f =>
        {
            var c = f.Client();
            var info = await c.PostAsJsonAsync("/api/v1/flight/sql/info", new { query = "SELECT value FROM default.main.air1" });
            using var infoDoc = await JsonOrFailAsync(info);
            var ticket = infoDoc.RootElement.GetProperty("ticket").GetRawText();
            var stream = await c.PostAsync("/api/v1/flight/sql/stream", new StringContent(ticket, Encoding.UTF8, "application/json"));
            if (stream.StatusCode != HttpStatusCode.OK)
            {
                throw new ShouldAssertException($"HTTP {(int)stream.StatusCode}: {await stream.Content.ReadAsStringAsync()}");
            }

            return await ArrowValuesAsync(await stream.Content.ReadAsStreamAsync());
        },
        "olap" => async f =>
        {
            var r = await f.Client().PostAsJsonAsync("/api/v1/olap/query", new { sql = "SELECT value FROM air1", tableNames = new[] { "default.main.air1" } });
            using var doc = await JsonOrFailAsync(r);
            return Numbers(doc.RootElement.GetProperty("rows"), doc.RootElement.GetProperty("columns"));
        },
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
    };

    private static async Task<JsonDocument> JsonOrFailAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new ShouldAssertException($"HTTP {(int)response.StatusCode}: {body}");
        }

        return JsonDocument.Parse(body);
    }

    /// <summary>A numeric reading of a JSON value; masked text (for example a redaction marker) has none.</summary>
    private static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    /// <summary>Numeric <c>value</c> cells of rows given as objects (property "value") or as arrays (position of "value" in <paramref name="columns"/>).</summary>
    private static IReadOnlyList<double> Numbers(JsonElement rows, JsonElement? columns)
    {
        var index = columns is { ValueKind: JsonValueKind.Array } cols
            ? cols.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetProperty("name").GetString()).ToList().IndexOf("value")
            : -1;
        var numbers = new List<double>();
        foreach (var row in rows.EnumerateArray())
        {
            var cell = row.ValueKind == JsonValueKind.Array
                ? (index >= 0 ? row[index] : default)
                : row.TryGetProperty("value", out var v) ? v : default;
            if (cell.ValueKind != JsonValueKind.Undefined && Number(cell) is { } number)
            {
                numbers.Add(number);
            }
        }

        return numbers;
    }

    private static async Task<IReadOnlyList<double>> ArrowValuesAsync(Stream body)
    {
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer);
        buffer.Position = 0;
        var numbers = new List<double>();
        using var reader = new ArrowStreamReader(buffer);
        while (await reader.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                if (batch.Length == 0)
                {
                    continue;
                }

                var column = batch.Column("value");
                for (var i = 0; i < batch.Length; i++)
                {
                    switch (column)
                    {
                        case DoubleArray a when a.GetValue(i) is { } d:
                            numbers.Add(d);
                            break;
                        case FloatArray a when a.GetValue(i) is { } f:
                            numbers.Add(f);
                            break;
                        case StringArray a when double.TryParse(a.GetString(i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                            numbers.Add(parsed);
                            break;
                    }
                }
            }
        }

        return numbers;
    }
}
