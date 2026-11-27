namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class WebSqlResultJsonTests
{
    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in cells)
        {
            row[key] = value;
        }

        return row;
    }

    private static GovernedSqlResult Fixture(
        ImmutableArray<bool> masks = default,
        IReadOnlyList<SqlResultColumn>? described = null,
        string[]? columns = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows = null,
        string? next = null,
        bool truncated = false)
    {
        columns ??= ["id", "email"];
        rows ??= [Row(("id", 1), ("email", "alice@example.com")), Row(("id", 2), ("email", null))];
        described ??=
        [
            new SqlResultColumn("id", typeof(int), AllowDbNull: false),
            new SqlResultColumn("email", typeof(string), AllowDbNull: false)
        ];
        return new GovernedSqlResult(
            "SELECT secret FROM t", columns, rows, rows.Count, 42, truncated, described, false,
            new GovernedSqlPageResponse(1, 0, 2), next, null,
            [new WebSqlIndexHint("t", "no index", ["email"])],
            default, masks);
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Serialize_MemberOrderEqualsWebSql()
    {
        var json = WebSqlResultJson.Serialize(Fixture(next: "tok"));

        Parse(json).Select(p => p.Key).ShouldBe(["columns", "rows", "rowCount", "truncated", "page", "next", "indexHints"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Serialize_ColumnsCarryTypeNullableMasked_MaskedIsNullable()
    {
        var root = Parse(WebSqlResultJson.Serialize(Fixture(masks: [false, true])));
        var columns = root["columns"]!.AsArray();

        columns[0]!["name"]!.GetValue<string>().ShouldBe("id");
        columns[0]!["type"]!.GetValue<string>().ShouldBe("integer");
        columns[0]!["nullable"]!.GetValue<bool>().ShouldBeFalse();
        columns[0]!["masked"]!.GetValue<bool>().ShouldBeFalse();
        columns[1]!["masked"]!.GetValue<bool>().ShouldBeTrue();
        columns[1]!["nullable"]!.GetValue<bool>().ShouldBeTrue("a masked column is always nullable, as on HTTP");
    }

    [Fact]
    public void Serialize_WithoutColumnDescriptions_IsVarcharNullable()
    {
        var result = Fixture() with { ColumnDescriptions = null };

        var column = Parse(WebSqlResultJson.Serialize(result))["columns"]![0]!;

        column["type"]!.GetValue<string>().ShouldBe("varchar");
        column["nullable"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Serialize_HasNoResultCacheOriginalSqlElapsedHasMoreCursor()
    {
        var json = WebSqlResultJson.Serialize(Fixture(next: "tok"));
        var root = Parse(json);

        foreach (var name in new[] { "resultCache", "originalSql", "elapsedMilliseconds", "hasMore", "cursor" })
        {
            root.ContainsKey(name).ShouldBeFalse(name);
        }

        json.ShouldNotContain("SELECT secret");
    }

    [Fact]
    public void Serialize_ValuesEqualWebSqlValueWriter()
    {
        var when = new DateTime(2026, 11, 27, 1, 2, 3, DateTimeKind.Utc);
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var values = new object?[] { 1L, 2.5d, "x\"y", when, guid, null, true, 12.50m, new byte[] { 1, 2 } };
        var columns = values.Select((_, i) => "c" + i).ToArray();
        var row = Row(columns.Select((c, i) => (c, values[i])).ToArray());
        var result = Fixture(columns: columns, rows: [row], described: columns.Select(c => new SqlResultColumn(c, null)).ToArray());

        var cells = Parse(WebSqlResultJson.Serialize(result))["rows"]![0]!.AsObject();

        for (var i = 0; i < values.Length; i++)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                WebSqlValueWriter.Write(w, values[i]);
            }

            JsonNode.DeepEquals(cells[columns[i]], JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray()))).ShouldBeTrue(columns[i]);
        }
    }

    [Fact]
    public void Serialize_NoResultSet_EmptyColumnsAndRows()
    {
        var result = new GovernedSqlResult("x", [], [], 0, 1);

        var root = Parse(WebSqlResultJson.Serialize(result));

        root["columns"]!.AsArray().Count.ShouldBe(0);
        root["rows"]!.AsArray().Count.ShouldBe(0);
        root["rowCount"]!.GetValue<int>().ShouldBe(0);
        root["truncated"]!.GetValue<bool>().ShouldBeFalse();
        root.ContainsKey("page").ShouldBeFalse();
        root.ContainsKey("next").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Serialize_MaskedEqualsHttpRule_UnknownIsFalse()
    {
        // Mask flags shorter than the columns: the unknown ones are not masked, never an exception.
        var shortMasks = Parse(WebSqlResultJson.Serialize(Fixture(masks: [true])))["columns"]!.AsArray();
        shortMasks[0]!["masked"]!.GetValue<bool>().ShouldBeTrue();
        shortMasks[1]!["masked"]!.GetValue<bool>().ShouldBeFalse();

        var unknown = Parse(WebSqlResultJson.Serialize(Fixture()))["columns"]!.AsArray();
        unknown.ShouldAllBe(c => !c!["masked"]!.GetValue<bool>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Serialize_OnlyResultColumns_NoHiddenColumnType()
    {
        var result = Fixture(
            columns: ["id"],
            rows: [Row(("id", 1), ("hidden", "secret"))],
            described: [new SqlResultColumn("id", typeof(int))]);

        var json = WebSqlResultJson.Serialize(result);

        Parse(json)["columns"]!.AsArray().Count.ShouldBe(1);
        json.ShouldNotContain("hidden");
        json.ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Serialize_DuplicateAndCaseCollidingNames_EqualHttpUniqueNames_NoValueOverwrite()
    {
        var unique = SqlResultColumns.MakeUnique(["a", "A", ""]);
        var rows = new IReadOnlyDictionary<string, object?>[] { Row((unique[0], 1), (unique[1], 2), (unique[2], 3)) };
        var result = Fixture(
            columns: unique,
            rows: rows,
            described: unique.Select(n => new SqlResultColumn(n, typeof(int))).ToArray());

        var root = Parse(WebSqlResultJson.Serialize(result));

        root["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ShouldBe(unique);
        var row = root["rows"]![0]!.AsObject();
        row.Select(p => p.Key).ShouldBe(unique);
        row.Select(p => p.Value!.GetValue<int>()).ShouldBe([1, 2, 3]);
    }

    private static string HttpShapedBody(GovernedSqlResult r)
    {
        // The endpoint's writer sequence over the shared member writers, with the endpoint's own writer options.
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartArray("columns");
            for (var i = 0; i < r.Columns.Count; i++)
            {
                var masked = !r.MaskedColumns.IsDefault && i < r.MaskedColumns.Length && r.MaskedColumns[i];
                WebSqlResultJson.WriteColumn(w, r.Columns[i], r.ColumnDescriptions![i], masked);
            }

            w.WriteEndArray();
            w.WriteStartArray("rows");
            foreach (var row in r.Rows)
            {
                w.WriteStartObject();
                foreach (var name in r.Columns)
                {
                    w.WritePropertyName(name);
                    WebSqlValueWriter.Write(w, row[name]);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            WebSqlResultJson.WritePaging(w, r.RowCount, r.Truncated, r.Page, r.Next, r.PagingUnavailable);
            WebSqlResultJson.WriteIndexHints(w, r.IndexHints);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void HttpAndMcp_SameResult_ProduceEqualBody()
    {
        var result = Fixture(masks: [false, true], next: "tok", truncated: true);

        JsonNode.DeepEquals(Parse(HttpShapedBody(result)), Parse(WebSqlResultJson.Serialize(result))).ShouldBeTrue();
    }

    [Fact]
    public async Task HttpEndpointOutput_ByteIdenticalAfterWriterMove()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("email", typeof(string));
        table.Rows.Add(1, "alice@example.com");
        var service = new TableService(table, [false, true]);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Path = "/api/v1/websql/query";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"sql":"SELECT id, email FROM users"}"""));
        context.Request.ContentLength = context.Request.Body.Length;
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user_1"), new Claim("tenant_id", "tenant_a")], "Test"));
        var body = new MemoryStream();
        context.Response.Body = body;

        await WebSqlEndpoints.HandleWebSqlRequest(context, service, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        Encoding.UTF8.GetString(body.ToArray()).ShouldBe(
            """{"columns":[{"name":"id","type":"integer","nullable":true,"masked":false},{"name":"email","type":"varchar","nullable":true,"masked":true}],"rows":[{"id":1,"email":"alice@example.com"}],"rowCount":1,"truncated":false}""");
    }

    private sealed class TableService(DataTable table, ImmutableArray<bool> masks) : IGovernedSqlExecutionService
    {
        public async Task ExecuteGovernedQueryAsync(
            GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId,
            Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter, CancellationToken ct = default)
        {
            using var reader = table.CreateDataReader();
            await rowWriter(reader, new GovernedResultInfo(SqlResultColumns.Describe(reader), masks), ct);
        }

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
