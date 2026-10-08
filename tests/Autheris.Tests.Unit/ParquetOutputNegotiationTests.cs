namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Api.Serialization;
using Autheris.Application.Serialization;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Parquet;
using Parquet.Schema;
using Shouldly;
using Xunit;

/// <summary>
/// F-DATA-01: Parquet output on all data egress channels via 'Accept: application/vnd.apache.parquet'.
/// </summary>
public sealed class ParquetOutputNegotiationTests
{
    private const string ParquetAccept = ParquetContentNegotiation.ParquetMediaType;
    private const string Tenant = "tenant_a";

    // =========================================================================
    // Helpers
    // =========================================================================

    private static GatewayOptions CreateOptions(
        int maxRows = 1000,
        string compression = "Snappy",
        long maxBufferedSourceBytes = 64 * 1024 * 1024,
        bool enabled = true,
        bool flatten = true)
    {
        return new GatewayOptions
        {
            ParquetEgress = new ParquetEgressOptions
            {
                Enabled = enabled,
                MaxRowsPerFile = maxRows,
                Compression = compression,
                MaxBufferedSourceBytes = maxBufferedSourceBytes,
                FlattenNestedStructures = flatten
            }
        };
    }

    private static ParquetExportService CreateService(GatewayOptions? options = null) =>
        new(Options.Create(options ?? CreateOptions()), NullLogger<ParquetExportService>.Instance);

    private static ParquetExportRequest CreateRequest(string table = "orders", IReadOnlyList<string>? columns = null, bool flatten = true) =>
        new(new TableIdentifier("sales", "public", table), columns ?? Array.Empty<string>(), FlattenNested: flatten);

    private static ServiceProvider BuildServices(GatewayOptions options)
    {
        return new ServiceCollection()
            .AddSingleton<IOptions<GatewayOptions>>(Options.Create(options))
            .AddSingleton<IParquetExportService>(CreateService(options))
            .BuildServiceProvider();
    }

    private static ClaimsPrincipal CreateUser()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-PARQUET-USER"),
            new("tenant_id", Tenant)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static DefaultHttpContext CreateHttpContext(IServiceProvider services, string? accept, string path = "/graphql", string method = "POST")
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            TraceIdentifier = "trace-parquet-1",
            User = CreateUser()
        };
        context.Request.Method = method;
        context.Request.Path = path;
        if (accept != null)
        {
            context.Request.Headers.Accept = accept;
        }
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static byte[] ResponseBytes(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();

    private static string ResponseText(HttpContext context) => Encoding.UTF8.GetString(ResponseBytes(context));

    private sealed record ParquetContent(
        IReadOnlyDictionary<string, DataField> Fields,
        IReadOnlyDictionary<string, object?[]> Columns,
        int RowGroupCount);

    private static async Task<ParquetContent> ReadParquetAsync(byte[] data)
    {
        using var stream = new MemoryStream(data);
        await using var reader = await ParquetReader.CreateAsync(stream);
        var dataFields = reader.Schema.GetDataFields();
        var fields = dataFields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var columns = dataFields.ToDictionary(f => f.Name, _ => Array.Empty<object?>(), StringComparer.Ordinal);

        if (reader.RowGroupCount > 0)
        {
            using var rowGroup = reader.OpenRowGroupReader(0);
            var rowCount = (int)rowGroup.RowCount;
            foreach (var field in dataFields)
            {
                columns[field.Name] = await ReadColumnDataAsync(rowGroup, field, rowCount);
            }
        }

        return new ParquetContent(fields, columns, reader.RowGroupCount);
    }

    private static async Task<object?[]> ReadColumnDataAsync(ParquetRowGroupReader rowGroup, DataField field, int rowCount)
    {
        if (field.ClrType == typeof(bool) || field.ClrType == typeof(bool?))
        {
            var mem = new bool?[rowCount];
            await rowGroup.ReadAsync(field, mem.AsMemory());
            return mem.Cast<object?>().ToArray();
        }
        if (field.ClrType == typeof(int) || field.ClrType == typeof(int?))
        {
            var mem = new int?[rowCount];
            await rowGroup.ReadAsync(field, mem.AsMemory());
            return mem.Cast<object?>().ToArray();
        }
        if (field.ClrType == typeof(long) || field.ClrType == typeof(long?))
        {
            var mem = new long?[rowCount];
            await rowGroup.ReadAsync(field, mem.AsMemory());
            return mem.Cast<object?>().ToArray();
        }
        if (field.ClrType == typeof(double) || field.ClrType == typeof(double?))
        {
            var mem = new double?[rowCount];
            await rowGroup.ReadAsync(field, mem.AsMemory());
            return mem.Cast<object?>().ToArray();
        }
        if (field.ClrType == typeof(decimal) || field.ClrType == typeof(decimal?))
        {
            var mem = new decimal?[rowCount];
            await rowGroup.ReadAsync(field, mem.AsMemory());
            return mem.Cast<object?>().ToArray();
        }
        if (field.ClrType == typeof(DateTime) || field.ClrType == typeof(DateTime?))
        {
            var mem = new DateTime?[rowCount];
            await rowGroup.ReadAsync(field, mem.AsMemory());
            return mem.Cast<object?>().ToArray();
        }
        if (field.ClrType == typeof(byte[]))
        {
            var mem = (byte[]?[])(Array)new byte[rowCount][];
            await rowGroup.ReadAsync(field, mem.AsMemory());
            return mem.Cast<object?>().ToArray();
        }

        var strMem = new string?[rowCount];
        await rowGroup.ReadAsync(field, strMem.AsMemory());
        return strMem.Cast<object?>().ToArray();
    }

    private static List<IReadOnlyDictionary<string, object?>> Rows(params Dictionary<string, object?>[] rows) =>
        rows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList();

    // =========================================================================
    // 1. Parquet writer (ParquetExportService)
    // =========================================================================

    [Fact]
    public async Task PARQ_Writer_RoundTrip_PreservesTypedValues()
    {
        var service = CreateService();
        var uid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
        var rows = Rows(
            new Dictionary<string, object?>
            {
                ["id"] = 1L,
                ["price"] = 12.5,
                ["amount"] = 12.34m,
                ["active"] = true,
                ["name"] = "Alice",
                ["created"] = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ["changed"] = new DateTimeOffset(2026, 1, 2, 5, 4, 5, TimeSpan.FromHours(2)),
                ["uid"] = uid,
                ["note"] = null
            },
            new Dictionary<string, object?>
            {
                ["id"] = 2L,
                ["price"] = null,
                ["amount"] = 7m,
                ["active"] = false,
                ["name"] = null,
                ["created"] = null,
                ["changed"] = null,
                ["uid"] = null,
                ["note"] = null
            });

        var result = await service.ExportToParquetAsync(CreateRequest(), rows);

        result.RowCount.ShouldBe(2);
        result.IsTruncated.ShouldBeFalse();
        result.ContentType.ShouldBe("application/vnd.apache.parquet");

        var content = await ReadParquetAsync(result.Data);
        content.RowGroupCount.ShouldBe(1);
        content.Fields["id"].ClrType.ShouldBe(typeof(long));
        content.Fields["price"].ClrType.ShouldBe(typeof(double));
        content.Fields["amount"].ClrType.ShouldBe(typeof(decimal));
        content.Fields["active"].ClrType.ShouldBe(typeof(bool));
        content.Fields["name"].ClrType.ShouldBeOneOf(typeof(string), typeof(ReadOnlyMemory<char>));
        content.Fields["created"].ClrType.ShouldBe(typeof(DateTime));
        content.Fields["changed"].ClrType.ShouldBe(typeof(DateTime));
        content.Fields["uid"].ClrType.ShouldBeOneOf(typeof(string), typeof(ReadOnlyMemory<char>));
        content.Fields["note"].ClrType.ShouldBeOneOf(typeof(string), typeof(ReadOnlyMemory<char>));

        content.Columns["id"].ShouldBe(new object?[] { 1L, 2L });
        content.Columns["price"].ShouldBe(new object?[] { 12.5, null });
        content.Columns["amount"].ShouldBe(new object?[] { 12.34m, 7m });
        content.Columns["active"].ShouldBe(new object?[] { true, false });
        content.Columns["name"].ShouldBe(new object?[] { "Alice", null });
        content.Columns["created"].ShouldBe(new object?[] { new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), null });
        content.Columns["changed"].ShouldBe(new object?[] { new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), null });
        content.Columns["uid"].ShouldBe(new object?[] { uid.ToString("D"), null });
        content.Columns["note"].ShouldBe(new object?[] { null, null });
    }

    [Fact]
    public async Task PARQ_Writer_MixedTypes_AreWidenedOverAllRows()
    {
        var service = CreateService();
        var rows = Rows(
            new Dictionary<string, object?> { ["ints"] = 1, ["intDec"] = 1L, ["intDouble"] = 1L, ["mixed"] = 1L, ["lateType"] = null },
            new Dictionary<string, object?> { ["ints"] = 2L, ["intDec"] = 2.5m, ["intDouble"] = 2.5, ["mixed"] = "x", ["lateType"] = 42 });

        var result = await service.ExportToParquetAsync(CreateRequest(), rows);
        var content = await ReadParquetAsync(result.Data);

        content.Fields["ints"].ClrType.ShouldBe(typeof(long));
        content.Columns["ints"].ShouldBe(new object?[] { 1L, 2L });
        content.Fields["intDec"].ClrType.ShouldBe(typeof(decimal));
        content.Columns["intDec"].ShouldBe(new object?[] { 1m, 2.5m });
        content.Fields["intDouble"].ClrType.ShouldBe(typeof(double));
        content.Columns["intDouble"].ShouldBe(new object?[] { 1.0, 2.5 });
        content.Fields["mixed"].ClrType.ShouldBeOneOf(typeof(string), typeof(ReadOnlyMemory<char>));
        content.Columns["mixed"].ShouldBe(new object?[] { "1", "x" });

        // The type is inferred over all rows, not only from row 0
        content.Fields["lateType"].ClrType.ShouldBe(typeof(int));
        content.Columns["lateType"].ShouldBe(new object?[] { null, 42 });
    }

    [Fact]
    public async Task PARQ_Writer_JsonElements_FlattenNestedObjects_AndArraysAsJsonString()
    {
        using var document = JsonDocument.Parse("""
            {"id": 5, "price": 1.5, "flag": true, "missing": null,
             "customer": {"name": "Alice", "address": {"city": "Bern"}},
             "tags": ["x", "y"]}
            """);
        var row = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);

        var result = await CreateService().ExportToParquetAsync(CreateRequest(), Rows(row));
        var content = await ReadParquetAsync(result.Data);

        content.Columns["id"].ShouldBe(new object?[] { 5L });
        content.Fields["price"].ClrType.ShouldBe(typeof(decimal));
        content.Columns["price"].ShouldBe(new object?[] { 1.5m });
        content.Columns["flag"].ShouldBe(new object?[] { true });
        content.Columns["missing"].ShouldBe(new object?[] { null });
        content.Columns["customer.name"].ShouldBe(new object?[] { "Alice" });
        content.Columns["customer.address.city"].ShouldBe(new object?[] { "Bern" });
        content.Columns["tags"].ShouldBe(new object?[] { "[\"x\", \"y\"]" });
        content.Fields.ContainsKey("customer").ShouldBeFalse();
    }

    [Fact]
    public async Task PARQ_Writer_WithoutFlattening_NestedObjectsBecomeJsonStrings()
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = 1L,
            ["customer"] = new Dictionary<string, object?> { ["name"] = "Alice" }
        };

        var result = await CreateService().ExportToParquetAsync(CreateRequest(flatten: false), Rows(row));
        var content = await ReadParquetAsync(result.Data);

        content.Columns["customer"].ShouldBe(new object?[] { "{\"name\":\"Alice\"}" });
        content.Fields.ContainsKey("customer.name").ShouldBeFalse();
    }

    [Fact]
    public async Task PARQ_Writer_Truncation_EnforcesMaxRowsPerFile()
    {
        var service = CreateService(CreateOptions(maxRows: 10));
        var rows = Enumerable.Range(1, 25)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = (long)i })
            .ToList();

        var result = await service.ExportToParquetAsync(CreateRequest(), rows);

        result.RowCount.ShouldBe(10);
        result.IsTruncated.ShouldBeTrue();
        var content = await ReadParquetAsync(result.Data);
        content.Columns["id"].Length.ShouldBe(10);
        content.Columns["id"][9].ShouldBe(10L);
    }

    [Fact]
    public async Task PARQ_Writer_ZeroRows_ProducesReadableFileWithSchema()
    {
        var result = await CreateService().ExportToParquetAsync(CreateRequest(columns: ["id", "name"]), []);

        result.RowCount.ShouldBe(0);
        result.IsTruncated.ShouldBeFalse();
        var content = await ReadParquetAsync(result.Data);
        content.RowGroupCount.ShouldBe(0);
        content.Fields.Keys.ShouldBe(new[] { "id", "name" }, ignoreOrder: true);
    }

    [Fact]
    public async Task PARQ_Writer_MaskedValues_StayVerbatim()
    {
        var rows = Rows(new Dictionary<string, object?>
        {
            ["id"] = 1001L,
            ["iban"] = "DE89 **** **** **** 1234",
            ["email"] = "j***@company.com",
            ["ssn"] = "HMAC:7f3a9c"
        });

        var result = await CreateService().ExportToParquetAsync(CreateRequest(table: "accounts"), rows);
        var content = await ReadParquetAsync(result.Data);

        content.Columns["iban"].ShouldBe(new object?[] { "DE89 **** **** **** 1234" });
        content.Columns["email"].ShouldBe(new object?[] { "j***@company.com" });
        content.Columns["ssn"].ShouldBe(new object?[] { "HMAC:7f3a9c" });
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Snappy")]
    [InlineData("Gzip")]
    [InlineData("not-a-codec")]
    public async Task PARQ_Writer_Compression_IsReadable(string compression)
    {
        var service = CreateService(CreateOptions(compression: compression));
        var rows = Enumerable.Range(1, 50)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = (long)i, ["text"] = "value-" + i })
            .ToList();

        var result = await service.ExportToParquetAsync(CreateRequest(), rows);
        var content = await ReadParquetAsync(result.Data);

        content.Columns["id"].Length.ShouldBe(50);
        content.Columns["text"][49].ShouldBe("value-50");
    }

    [Fact]
    public void PARQ_Writer_CompressionOption_DefaultsToSnappy()
    {
        new ParquetEgressOptions().Compression.ShouldBe("Snappy");
        new ParquetEgressOptions().MaxBufferedSourceBytes.ShouldBe(64L * 1024 * 1024);
    }

    [Theory]
    [InlineData("schema/table")]
    [InlineData("table; DROP TABLE users;--")]
    [InlineData("table name")]
    public async Task PARQ_Writer_UnsafeTableName_IsStillRejected(string tableName)
    {
        var service = CreateService();

        await Should.ThrowAsync<ArgumentException>(() =>
            service.ExportToParquetAsync(new ParquetExportRequest(new TableIdentifier("sales", "public", tableName), ["id"]), []));
    }

    [Fact]
    public async Task PARQ_Writer_ColumnNameWithControlCharacters_IsRejected()
    {
        var rows = Rows(new Dictionary<string, object?> { ["bad\r\nname"] = 1L });

        await Should.ThrowAsync<ArgumentException>(() => CreateService().ExportToParquetAsync(CreateRequest(), rows));
    }

    [Fact]
    public async Task PARQ_Writer_Disabled_Throws()
    {
        var service = CreateService(CreateOptions(enabled: false));

        await Should.ThrowAsync<InvalidOperationException>(() => service.ExportToParquetAsync(CreateRequest(), []));
    }

    // =========================================================================
    // 2. Content negotiation
    // =========================================================================

    [Theory]
    [InlineData("application/vnd.apache.parquet", true)]
    [InlineData("application/x-parquet", true)]
    [InlineData("APPLICATION/VND.APACHE.PARQUET", true)]
    [InlineData("application/vnd.apache.parquet, */*", true)]
    [InlineData("application/json;q=0.5, application/vnd.apache.parquet", true)]
    [InlineData("application/json, application/vnd.apache.parquet;q=0.5", false)]
    [InlineData("text/html, application/vnd.apache.parquet;q=0.9", false)]
    [InlineData("application/vnd.apache.parquet;q=0", false)]
    [InlineData("*/*", false)]
    [InlineData("application/*", false)]
    [InlineData("application/json", false)]
    [InlineData("", false)]
    public void PARQ_Negotiation_AcceptHeaderVariants(string accept, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = accept;

        ParquetContentNegotiation.IsParquetRequested(context.Request).ShouldBe(expected);
    }

    [Fact]
    public void PARQ_Negotiation_NoAcceptHeader_IsNotParquet()
    {
        ParquetContentNegotiation.IsParquetRequested(new DefaultHttpContext().Request).ShouldBeFalse();
    }

    [Theory]
    [InlineData("application/vnd.apache.parquet", false)]
    [InlineData("application/vnd.apache.parquet, application/json;q=0.1", true)]
    [InlineData("application/vnd.apache.parquet, application/graphql-response+json;q=0.1", true)]
    [InlineData("application/vnd.apache.parquet, */*;q=0.1", true)]
    [InlineData("application/vnd.apache.parquet, application/json;q=0", false)]
    [InlineData("application/vnd.apache.parquet, text/csv;q=0.5", false)]
    public void PARQ_Negotiation_DetectsJsonAlternative(string accept, bool expectedAlternative)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = accept;

        var evaluation = ParquetContentNegotiation.Evaluate(context.Request);

        evaluation.ParquetPreferred.ShouldBeTrue();
        evaluation.HasJsonAlternative.ShouldBe(expectedAlternative);
    }

    // =========================================================================
    // 3. GraphQL middleware
    // =========================================================================

    private static ParquetGraphQLResponseMiddleware CreateMiddleware(GatewayOptions options, RequestDelegate next) =>
        new(next, Options.Create(options), CreateService(options), NullLogger<ParquetGraphQLResponseMiddleware>.Instance);

    private static RequestDelegate GraphQLNext(
        string json,
        int statusCode = StatusCodes.Status200OK,
        string contentType = "application/graphql-response+json; charset=utf-8",
        Action<HttpContext>? inspect = null)
    {
        return async ctx =>
        {
            inspect?.Invoke(ctx);
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = contentType;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json), ctx.RequestAborted);
        };
    }

    private static string TableRecordPayloadJson() => JsonSerializer.Serialize(new
    {
        data = new
        {
            table = new
            {
                tableName = "sales.dbo.customers",
                totalCount = 2,
                jsonRows = new[]
                {
                    JsonSerializer.Serialize(new { id = 1, customer = "Alice", ssn = "***", address = new { city = "Bern" } }),
                    JsonSerializer.Serialize(new { id = 2, customer = "Bob", ssn = "***", address = new { city = "Basel" } })
                }
            }
        }
    });

    [Fact]
    public async Task PARQ_GraphQL_JsonRowsPayload_IsConvertedToReadableParquet()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept);
        string? downstreamAccept = null;
        var middleware = CreateMiddleware(options, GraphQLNext(TableRecordPayloadJson(), inspect: ctx => downstreamAccept = ctx.Request.Headers.Accept.ToString()));

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.ContentType.ShouldBe(ParquetAccept);
        context.Response.Headers["X-Row-Count"].ToString().ShouldBe("2");
        context.Response.Headers["X-Export-Truncated"].ToString().ShouldBe("false");
        context.Response.Headers.ContentDisposition.ToString().ShouldContain("attachment");
        context.Response.Headers.ContentDisposition.ToString().ShouldContain("table_");
        context.Response.Headers.Vary.ToString().ShouldContain("Accept");
        context.Response.Headers.CacheControl.ToString().ShouldBe("no-store");

        // The GraphQL server (and egress interceptors) saw a plain JSON request
        downstreamAccept.ShouldBe("application/graphql-response+json, application/json");
        context.Request.Headers.Accept.ToString().ShouldBe(ParquetAccept);

        var content = await ReadParquetAsync(ResponseBytes(context));
        content.Columns["id"].ShouldBe(new object?[] { 1L, 2L });
        content.Columns["customer"].ShouldBe(new object?[] { "Alice", "Bob" });
        content.Columns["ssn"].ShouldBe(new object?[] { "***", "***" });
        content.Columns["address.city"].ShouldBe(new object?[] { "Bern", "Basel" });
    }

    [Fact]
    public async Task PARQ_GraphQL_ListOfObjectsAndEdges_AreConverted()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);

        var listContext = CreateHttpContext(services, ParquetAccept);
        await CreateMiddleware(options, GraphQLNext("""{"data":{"orders":[{"id":1,"total":9.5},{"id":2,"total":3.25}]}}""")).InvokeAsync(listContext);
        var listContent = await ReadParquetAsync(ResponseBytes(listContext));
        listContent.Columns["id"].ShouldBe(new object?[] { 1L, 2L });
        listContent.Columns["total"].ShouldBe(new object?[] { 9.5m, 3.25m });

        var edgeContext = CreateHttpContext(services, ParquetAccept);
        await CreateMiddleware(options, GraphQLNext("""{"data":{"orders":{"edges":[{"node":{"id":7}},{"node":{"id":8}}]}}}""")).InvokeAsync(edgeContext);
        var edgeContent = await ReadParquetAsync(ResponseBytes(edgeContext));
        edgeContent.Columns["id"].ShouldBe(new object?[] { 7L, 8L });
    }

    [Fact]
    public async Task PARQ_GraphQL_Errors_AreReturnedAsOriginalJson()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept);
        const string json = """{"errors":[{"message":"Access denied by policy"}],"data":null}""";

        await CreateMiddleware(options, GraphQLNext(json)).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.ContentType!.ShouldStartWith("application/graphql-response+json");
        context.Response.Headers["X-Parquet-Conversion"].ToString().ShouldBe("skipped-errors");
        ResponseText(context).ShouldBe(json);
    }

    [Fact]
    public async Task PARQ_GraphQL_MultipleRootFields_Returns406()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept);

        await CreateMiddleware(options, GraphQLNext("""{"data":{"a":[{"x":1}],"b":[{"y":2}]}}""")).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status406NotAcceptable);
        ResponseText(context).ShouldContain("exactly one root field");
    }

    [Fact]
    public async Task PARQ_GraphQL_ScalarRootField_Returns406()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept);

        await CreateMiddleware(options, GraphQLNext("""{"data":{"__typename":"Query","count":5}}""")).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status406NotAcceptable);
        ResponseText(context).ShouldContain("scalar");
    }

    [Fact]
    public async Task PARQ_GraphQL_NonOkStatus_IsPassedThroughUnchanged()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept);
        const string json = """{"errors":[{"message":"Syntax Error"}]}""";

        await CreateMiddleware(options, GraphQLNext(json, StatusCodes.Status400BadRequest)).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        ResponseText(context).ShouldBe(json);
    }

    [Fact]
    public async Task PARQ_GraphQL_SourceBufferLimit_Returns413()
    {
        var options = CreateOptions(maxBufferedSourceBytes: 64);
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept);

        await CreateMiddleware(options, GraphQLNext(TableRecordPayloadJson())).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        var text = ResponseText(context);
        text.ShouldContain("Parquet conversion limit");
        text.ShouldNotContain("Alice");
    }

    [Fact]
    public async Task PARQ_GraphQL_WithoutParquetAccept_IsNotTouched()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, "application/json");
        string? downstreamAccept = null;
        var json = TableRecordPayloadJson();

        await CreateMiddleware(options, GraphQLNext(json, inspect: ctx => downstreamAccept = ctx.Request.Headers.Accept.ToString())).InvokeAsync(context);

        downstreamAccept.ShouldBe("application/json");
        ResponseText(context).ShouldBe(json);
    }

    [Fact]
    public async Task PARQ_Route_WithoutMarker_ParquetOnly_Returns406()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept, path: "/api/v1/governance/policies", method: "GET");
        var nextInvoked = false;

        await CreateMiddleware(options, _ => { nextInvoked = true; return Task.CompletedTask; }).InvokeAsync(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status406NotAcceptable);
        ResponseText(context).ShouldContain("supported");
    }

    [Fact]
    public async Task PARQ_Route_WithoutMarker_WithJsonAlternative_IsPassedThrough()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept + ", application/json;q=0.5", path: "/api/v1/governance/policies", method: "GET");
        var nextInvoked = false;

        await CreateMiddleware(options, _ => { nextInvoked = true; return Task.CompletedTask; }).InvokeAsync(context);

        nextInvoked.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task PARQ_Route_WithMarker_IsPassedThrough()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept, path: "/api/sql");
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ParquetOutputSupportedMetadata()), "websql"));
        var nextInvoked = false;

        await CreateMiddleware(options, _ => { nextInvoked = true; return Task.CompletedTask; }).InvokeAsync(context);

        nextInvoked.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/sse")]
    [InlineData("/api/webhooks/jira")]
    [InlineData("/health/ready")]
    public async Task PARQ_ExcludedRoutes_AreNeverConvertedOrRejected(string path)
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept, path: path);
        var nextInvoked = false;

        await CreateMiddleware(options, _ => { nextInvoked = true; return Task.CompletedTask; }).InvokeAsync(context);

        nextInvoked.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    // =========================================================================
    // 4. WebSQL / SQL endpoints / OData
    // =========================================================================

    private static System.Data.DataTable CreateEmployeeTable(int rowCount)
    {
        var table = new System.Data.DataTable("employees");
        table.Columns.Add("id", typeof(long));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("ssn", typeof(string));
        for (var i = 1; i <= rowCount; i++)
        {
            // The governed (rewritten) SQL already delivers the masked value
            table.Rows.Add((long)i, "Employee " + i, "***");
        }
        return table;
    }

    private sealed class DataTableSqlService(System.Data.DataTable table, Exception? toThrow = null) : IGovernedSqlExecutionService
    {
        public int Executions { get; private set; }

        public GovernedSqlQueryRequest? LastRequest { get; private set; }

        public async Task ExecuteGovernedQueryAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, Func<DbDataReader, CancellationToken, Task> rowWriter, CancellationToken ct = default)
        {
            Executions++;
            LastRequest = request;
            if (toThrow != null)
            {
                throw toThrow;
            }

            using var reader = table.CreateDataReader();
            await rowWriter(reader, ct);
        }

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> RewriteSqlAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static DefaultHttpContext CreateWebSqlContext(IServiceProvider services, string accept)
    {
        var context = CreateHttpContext(services, accept, path: "/api/sql");
        context.Request.ContentType = "text/plain";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT id, name, ssn FROM employees"));
        return context;
    }

    [Fact]
    public async Task PARQ_WebSql_ParquetAccept_ReturnsReadableParquet_WithMaskedValues()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateWebSqlContext(services, ParquetAccept);

        await WebSqlEndpoints.HandleWebSqlRequest(context, new DataTableSqlService(CreateEmployeeTable(3)), Options.Create(options), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.ContentType.ShouldBe(ParquetAccept);
        context.Response.Headers["X-Row-Count"].ToString().ShouldBe("3");
        var content = await ReadParquetAsync(ResponseBytes(context));
        content.Columns["id"].ShouldBe(new object?[] { 1L, 2L, 3L });
        content.Columns["ssn"].ShouldBe(new object?[] { "***", "***", "***" });
    }

    [Fact]
    public async Task PARQ_WebSql_MoreRowsThanLimit_IsTruncated()
    {
        var options = CreateOptions(maxRows: 3);
        using var services = BuildServices(options);
        var context = CreateWebSqlContext(services, ParquetAccept);

        await WebSqlEndpoints.HandleWebSqlRequest(context, new DataTableSqlService(CreateEmployeeTable(10)), Options.Create(options), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers["X-Row-Count"].ToString().ShouldBe("3");
        context.Response.Headers["X-Export-Truncated"].ToString().ShouldBe("true");
        var content = await ReadParquetAsync(ResponseBytes(context));
        content.Columns["id"].ShouldBe(new object?[] { 1L, 2L, 3L });
    }

    [Fact]
    public async Task PARQ_WebSql_EmptyResult_ReturnsParquetWithColumns()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateWebSqlContext(services, ParquetAccept);

        await WebSqlEndpoints.HandleWebSqlRequest(context, new DataTableSqlService(CreateEmployeeTable(0)), Options.Create(options), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers["X-Row-Count"].ToString().ShouldBe("0");
        var content = await ReadParquetAsync(ResponseBytes(context));
        content.RowGroupCount.ShouldBe(0);
        content.Fields.Keys.ShouldBe(new[] { "id", "name", "ssn" }, ignoreOrder: true);
    }

    [Fact]
    public async Task PARQ_WebSql_PolicyError_StaysJson403()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateWebSqlContext(services, ParquetAccept);

        await WebSqlEndpoints.HandleWebSqlRequest(
            context,
            new DataTableSqlService(CreateEmployeeTable(1), new WebSqlPolicyException("DDL statements are strictly forbidden in WebSQL.")),
            Options.Create(options),
            NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.ContentType!.ShouldStartWith("application/json");
        ResponseText(context).ShouldContain("DDL statements");
    }

    [Fact]
    public async Task PARQ_WebSql_ParquetDisabled_Returns406_WithoutExecutingQuery()
    {
        var options = CreateOptions(enabled: false);
        using var services = BuildServices(options);
        var context = CreateWebSqlContext(services, ParquetAccept);
        var sqlService = new DataTableSqlService(CreateEmployeeTable(1));

        await WebSqlEndpoints.HandleWebSqlRequest(context, sqlService, Options.Create(options), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status406NotAcceptable);
        sqlService.Executions.ShouldBe(0);
    }

    [Fact]
    public async Task PARQ_WebSql_ResultCutByWebSqlRowLimit_IsReportedAsTruncated()
    {
        // WebSQL findings 4.2: the WebSQL limit (MaxAllowedRows) cuts the result in the rewritten SQL, below the Parquet
        // file limit. The export must report it like the JSON path does (truncated: true / X-Autheris-Truncated).
        var options = new GatewayOptions
        {
            ParquetEgress = CreateOptions(maxRows: 100).ParquetEgress,
            WebSql = new WebSqlOptions { Enabled = true, MaxAllowedRows = 5, DefaultMaxRows = 5 }
        };
        using var services = BuildServices(options);
        var context = CreateWebSqlContext(services, ParquetAccept);

        await WebSqlEndpoints.HandleWebSqlRequest(context, new DataTableSqlService(CreateEmployeeTable(5)), Options.Create(options), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers["X-Row-Count"].ToString().ShouldBe("5");
        context.Response.Headers["X-Export-Truncated"].ToString().ShouldBe("true");
        context.Response.Headers["X-Autheris-Truncated"].ToString().ShouldBe("true");
    }

    [Fact]
    public async Task PARQ_WebSql_UsesParquetRowLimits()
    {
        // Gateway:RowLimits:Parquet raises the WebSQL limit for Parquet exports; reaching 5 rows is then not a cut.
        var options = new GatewayOptions
        {
            ParquetEgress = CreateOptions(maxRows: 100).ParquetEgress,
            WebSql = new WebSqlOptions { Enabled = true, MaxAllowedRows = 5, DefaultMaxRows = 5 },
            RowLimits = new TransportRowLimitsOptions { Parquet = new ChannelRowLimitOptions { DefaultMaxRows = 50, MaxAllowedRows = 50 } }
        };
        using var services = BuildServices(options);
        var context = CreateWebSqlContext(services, ParquetAccept);
        var sqlService = new DataTableSqlService(CreateEmployeeTable(5));

        await WebSqlEndpoints.HandleWebSqlRequest(context, sqlService, Options.Create(options), NullLoggerFactory.Instance);

        sqlService.LastRequest!.RowLimit.ShouldBe(new SqlRowLimit(50, 50));
        context.Response.Headers["X-Export-Truncated"].ToString().ShouldBe("false");
    }

    [Fact]
    public async Task PARQ_WebSql_ResultBelowWebSqlRowLimit_IsNotTruncated()
    {
        var options = new GatewayOptions
        {
            ParquetEgress = CreateOptions(maxRows: 100).ParquetEgress,
            WebSql = new WebSqlOptions { Enabled = true, MaxAllowedRows = 5, DefaultMaxRows = 5 }
        };
        using var services = BuildServices(options);
        var context = CreateWebSqlContext(services, ParquetAccept);

        await WebSqlEndpoints.HandleWebSqlRequest(context, new DataTableSqlService(CreateEmployeeTable(4)), Options.Create(options), NullLoggerFactory.Instance);

        context.Response.Headers["X-Export-Truncated"].ToString().ShouldBe("false");
        context.Response.Headers.ContainsKey("X-Autheris-Truncated").ShouldBeFalse();
    }

    [Fact]
    public async Task PARQ_SqlEndpoint_TruncatedResult_IsReportedAsTruncated()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept, path: "/api/v1/queries/active_customers", method: "GET");
        var rows = Rows(new Dictionary<string, object?> { ["id"] = 1 }, new Dictionary<string, object?> { ["id"] = 2 });
        var execution = CreateSqlEndpointService(new GovernedSqlResult("SELECT 1", "SELECT 1", ["id"], rows, 2, 3, Truncated: true));

        await SqlEndpointRoutes.HandleGetEndpoint("active_customers", context, execution, Options.Create(options), NullLoggerFactory.Instance);

        context.Response.Headers["X-Export-Truncated"].ToString().ShouldBe("true");
        context.Response.Headers["X-Autheris-Truncated"].ToString().ShouldBe("true");
    }

    private static ISqlEndpointExecutionService CreateSqlEndpointService(GovernedSqlResult result)
    {
        var service = Substitute.For<ISqlEndpointExecutionService>();
        service.ExecuteEndpointAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TenantId>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return service;
    }

    [Fact]
    public async Task PARQ_SqlEndpoint_ParquetAccept_ReturnsReadableParquet_WithMaskedValues()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept, path: "/api/v1/queries/active_customers", method: "GET");
        var rows = Rows(
            new Dictionary<string, object?> { ["id"] = 1, ["email"] = "a***@company.com" },
            new Dictionary<string, object?> { ["id"] = 2, ["email"] = "b***@company.com" });
        var execution = CreateSqlEndpointService(new GovernedSqlResult("SELECT 1", "SELECT 1", ["id", "email"], rows, 2, 3));

        await SqlEndpointRoutes.HandleGetEndpoint("active_customers", context, execution, Options.Create(options), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.ContentType.ShouldBe(ParquetAccept);
        context.Response.Headers.ContentDisposition.ToString().ShouldContain("active_customers_");
        var content = await ReadParquetAsync(ResponseBytes(context));
        content.Columns["id"].ShouldBe(new object?[] { 1, 2 });
        content.Columns["email"].ShouldBe(new object?[] { "a***@company.com", "b***@company.com" });
    }

    [Fact]
    public async Task PARQ_SqlEndpoint_ParquetDisabled_Returns406_WithoutExecuting()
    {
        var options = CreateOptions(enabled: false);
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept, path: "/api/v1/queries/active_customers", method: "GET");
        var execution = CreateSqlEndpointService(new GovernedSqlResult("SELECT 1", "SELECT 1", ["id"], Rows(), 0, 1));

        await SqlEndpointRoutes.HandleGetEndpoint("active_customers", context, execution, Options.Create(options), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status406NotAcceptable);
        await execution.DidNotReceive().ExecuteEndpointAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<CancellationToken>());
    }

    private static IODataHandler CreateODataHandler(ODataQueryResult result)
    {
        var handler = Substitute.For<IODataHandler>();
        handler.ExecuteEntitySetQueryAsync(
                Arg.Any<ClaimsPrincipal?>(),
                Arg.Any<string>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return handler;
    }

    [Fact]
    public async Task PARQ_OData_ParquetAccept_ReturnsRowsWithoutAnnotations()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept, path: "/odata/v4/sales/public/customers", method: "GET");
        var table = new TableIdentifier("sales", "public", "customers");
        var rows = Rows(new Dictionary<string, object?> { ["id"] = 1L, ["email"] = "j***@company.com" });
        var payload = ODataResponseFormatter.FormatEntitySetResponse("http://localhost/odata/v4", table, rows, 1);
        var handler = CreateODataHandler(new ODataQueryResult(true, StatusCodes.Status200OK, payload));

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "public", "customers", handler, context);

        result.ShouldBeOfType<EmptyHttpResult>();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.ContentType.ShouldBe(ParquetAccept);
        var content = await ReadParquetAsync(ResponseBytes(context));
        content.Fields.Keys.ShouldBe(new[] { "id", "email" }, ignoreOrder: true);
        content.Columns["email"].ShouldBe(new object?[] { "j***@company.com" });
    }

    [Fact]
    public async Task PARQ_OData_ErrorResult_StaysJson()
    {
        var options = CreateOptions();
        using var services = BuildServices(options);
        var context = CreateHttpContext(services, ParquetAccept, path: "/odata/v4/sales/public/customers", method: "GET");
        var payload = ODataResponseFormatter.FormatErrorResponse("ACCESS_DENIED", "Access denied by gateway governance policy.");
        var handler = CreateODataHandler(new ODataQueryResult(false, StatusCodes.Status403Forbidden, payload, "ACCESS_DENIED"));

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "public", "customers", handler, context);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.ContentType.ShouldNotBe(ParquetAccept);
        ResponseBytes(context).Length.ShouldBe(0);
    }
}
