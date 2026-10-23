namespace Autheris.Tests.Unit;

using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#293 GAP-001 (SEC-PG-11): WebSQL request validation answers a stable 400 with a catalogue code and a fixed message.</summary>
public sealed class WebSqlRequestValidationTests
{
    private static async Task<(int Status, JsonElement Body, string Raw)> PostAsync(string body, string contentType = "application/json")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/sql";
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();

        await WebSqlEndpoints.HandleWebSqlRequest(
            context,
            Substitute.For<IGovernedSqlExecutionService>(),
            Options.Create(new GatewayOptions()),
            NullLoggerFactory.Instance);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var doc = JsonDocument.Parse(raw);
        return (context.Response.StatusCode, doc.RootElement.Clone(), raw);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"sql\":123}")]
    [InlineData("{\"sql\":[\"select 1\"]}")]
    [InlineData("{\"sql\":\"select 1\",\"parameters\":\"x\"}")]
    [InlineData("{\"sql\":\"select 1\",\"parameters\":[1,2]}")]
    [InlineData("{\"sql\":\"select 1\",\"dataSource\":5}")]
    public async Task MalformedOrWronglyTypedBody_IsInvalidJson(string body)
    {
        var (status, json, raw) = await PostAsync(body);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        json.GetProperty("error").GetString().ShouldBe("BadRequest");
        json.GetProperty("code").GetString().ShouldBe("INVALID_JSON");
        json.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        json.TryGetProperty("traceId", out _).ShouldBeTrue();
        AssertNoInternals(raw);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"sql\":null}")]
    [InlineData("{\"sql\":\"\"}")]
    [InlineData("{\"sql\":\"   \"}")]
    [InlineData("{\"parameters\":{}}")]
    public async Task AbsentNullEmptyOrBlankSql_IsMissingRequiredField(string body)
    {
        var (status, json, raw) = await PostAsync(body);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        json.GetProperty("error").GetString().ShouldBe("BadRequest");
        json.GetProperty("code").GetString().ShouldBe("MISSING_REQUIRED_FIELD");
        json.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        json.TryGetProperty("traceId", out _).ShouldBeTrue();
        AssertNoInternals(raw);
    }

    [Fact]
    public async Task EmptyPlainTextBody_IsMissingRequiredField()
    {
        var (status, json, _) = await PostAsync("  ", "text/plain");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        json.GetProperty("code").GetString().ShouldBe("MISSING_REQUIRED_FIELD");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_PG_11_Messages_AreFixed_AndLeakNoParserTextPathOrType()
    {
        var (_, invalid, rawInvalid) = await PostAsync("{\"sql\":123,\"secret\":");
        var (_, otherInvalid, _) = await PostAsync("{\"sql\":\"select 1\",\"parameters\":\"x\"}");
        var (_, missing, rawMissing) = await PostAsync("{}");

        // The message does not depend on where the parser stopped.
        otherInvalid.GetProperty("message").GetString().ShouldBe(invalid.GetProperty("message").GetString());
        AssertNoInternals(rawInvalid);
        AssertNoInternals(rawMissing);
        missing.GetProperty("message").GetString().ShouldNotBe(invalid.GetProperty("message").GetString());
    }

    private static void AssertNoInternals(string raw)
    {
        foreach (var internalText in new[] { "System.", "Utf8", "JsonException", "LineNumber", "BytePositionInLine", "Path:", "$.", "WebSqlRequestDto", "Exception" })
        {
            raw.ShouldNotContain(internalText);
        }
    }
}
