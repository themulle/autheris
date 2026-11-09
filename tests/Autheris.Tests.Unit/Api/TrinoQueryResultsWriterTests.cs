namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

public sealed class TrinoQueryResultsWriterTests
{
    [Theory]
    [InlineData("5s", 5000)]
    [InlineData("500ms", 500)]
    [InlineData("1m", 60000)]
    [InlineData("2000", 2000)]
    [InlineData("10", 10000)]
    public void ParseDuration_ValidInputs_ReturnsExpectedDuration(string input, double expectedMs)
    {
        var duration = TrinoQueryResultsWriter.ParseDuration(input);
        duration.TotalMilliseconds.ShouldBe(expectedMs);
    }

    [Fact]
    public async Task Finished_SinglePage_HasColumnsDataAndNoNextUri()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("gateway.example.com");
        context.Response.Body = new MemoryStream();

        var status = new StatementExecutionStatus(
            "stmt-1",
            "FINISHED",
            ["id", "name"],
            [[1, "alice"], [2, "bob"]],
            15);

        await TrinoQueryResultsWriter.WriteAsync(context, status, token: 0);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        root.GetProperty("id").GetString().ShouldBe("stmt-1");
        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FINISHED");
        root.TryGetProperty("nextUri", out _).ShouldBeFalse();

        var columns = root.GetProperty("columns");
        columns.GetArrayLength().ShouldBe(2);
        columns[0].GetProperty("name").GetString().ShouldBe("id");
        columns[1].GetProperty("name").GetString().ShouldBe("name");

        var data = root.GetProperty("data");
        data.GetArrayLength().ShouldBe(2);
        data[0][0].GetInt32().ShouldBe(1);
        data[0][1].GetString().ShouldBe("alice");
    }

    [Fact]
    public async Task LargeResult_IsPaged_ByPageRows_WithIncrementingTokens()
    {
        var rows = Enumerable.Range(0, 10000)
            .Select(i => (IReadOnlyList<object?>)new object?[] { i, $"val-{i}" })
            .ToList();

        var status = new StatementExecutionStatus(
            "stmt-large",
            "FINISHED",
            ["id", "val"],
            rows,
            50);

        // Page 0 (token 0)
        var ctx0 = new DefaultHttpContext();
        ctx0.Request.Scheme = "https";
        ctx0.Request.Host = new HostString("gateway.example.com");
        ctx0.Response.Body = new MemoryStream();
        await TrinoQueryResultsWriter.WriteAsync(ctx0, status, token: 0);

        ctx0.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc0 = await JsonDocument.ParseAsync(ctx0.Response.Body);
        doc0.RootElement.GetProperty("stats").GetProperty("state").GetString().ShouldBe("RUNNING");
        doc0.RootElement.GetProperty("nextUri").GetString().ShouldBe("https://gateway.example.com/v1/statement/executing/stmt-large/1");
        doc0.RootElement.GetProperty("data").GetArrayLength().ShouldBe(4096);

        // Page 1 (token 1)
        var ctx1 = new DefaultHttpContext();
        ctx1.Request.Scheme = "https";
        ctx1.Request.Host = new HostString("gateway.example.com");
        ctx1.Response.Body = new MemoryStream();
        await TrinoQueryResultsWriter.WriteAsync(ctx1, status, token: 1);

        ctx1.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc1 = await JsonDocument.ParseAsync(ctx1.Response.Body);
        doc1.RootElement.GetProperty("stats").GetProperty("state").GetString().ShouldBe("RUNNING");
        doc1.RootElement.GetProperty("nextUri").GetString().ShouldBe("https://gateway.example.com/v1/statement/executing/stmt-large/2");
        doc1.RootElement.GetProperty("data").GetArrayLength().ShouldBe(4096);

        // Page 2 (token 2)
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Scheme = "https";
        ctx2.Request.Host = new HostString("gateway.example.com");
        ctx2.Response.Body = new MemoryStream();
        await TrinoQueryResultsWriter.WriteAsync(ctx2, status, token: 2);

        ctx2.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc2 = await JsonDocument.ParseAsync(ctx2.Response.Body);
        doc2.RootElement.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FINISHED");
        doc2.RootElement.TryGetProperty("nextUri", out _).ShouldBeFalse();
        doc2.RootElement.GetProperty("data").GetArrayLength().ShouldBe(1808);
    }

    [Fact]
    public async Task NextUri_IsAbsolute_AndUsesForwardedSchemeAndHost()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("public-proxy.company.net", 8443);
        context.Request.PathBase = new PathString("/gateway");
        context.Response.Body = new MemoryStream();

        var rows = Enumerable.Range(0, 5000)
            .Select(i => (IReadOnlyList<object?>)new object?[] { i })
            .ToList();
        var status = new StatementExecutionStatus("stmt-fwd", "FINISHED", ["id"], rows, 20);

        await TrinoQueryResultsWriter.WriteAsync(context, status, token: 0);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        var nextUri = root.GetProperty("nextUri").GetString();
        nextUri.ShouldBe("https://public-proxy.company.net:8443/gateway/v1/statement/executing/stmt-fwd/1");

        var infoUri = root.GetProperty("infoUri").GetString();
        infoUri.ShouldBe("https://public-proxy.company.net:8443/gateway/v1/statement/info/stmt-fwd");
    }

    [Fact]
    public async Task Canceled_IsReportedAsFailed_UserCanceled()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var status = new StatementExecutionStatus(
            "stmt-cancel",
            "CANCELED",
            null,
            null,
            50,
            Failure: new SqlFailure(SqlFailureKind.Canceled, SqlFailureClassifier.ClientCanceledMessage));

        await TrinoQueryResultsWriter.WriteAsync(context, status, token: 0);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FAILED");
        var error = root.GetProperty("error");
        error.GetProperty("errorType").GetString().ShouldBe("USER_ERROR");
        error.GetProperty("errorCode").GetInt32().ShouldBe(3);
        error.GetProperty("errorName").GetString().ShouldBe("USER_CANCELED");
        error.GetProperty("message").GetString().ShouldBe(SqlFailureClassifier.ClientCanceledMessage);
    }

    [Theory]
    [InlineData(SqlFailureKind.InvalidQuery, "USER_ERROR", 1, "SYNTAX_ERROR")]
    [InlineData(SqlFailureKind.PolicyDenied, "USER_ERROR", 4, "PERMISSION_DENIED")]
    [InlineData(SqlFailureKind.TransportDenied, "USER_ERROR", 4, "TRANSPORT_NOT_ALLOWED")]
    [InlineData(SqlFailureKind.NotSupported, "USER_ERROR", 13, "NOT_SUPPORTED")]
    [InlineData(SqlFailureKind.Canceled, "USER_ERROR", 3, "USER_CANCELED")]
    [InlineData(SqlFailureKind.Timeout, "INSUFFICIENT_RESOURCES", 131074, "EXCEEDED_TIME_LIMIT")]
    [InlineData(SqlFailureKind.Throttled, "INSUFFICIENT_RESOURCES", 131073, "QUERY_QUEUE_FULL")]
    [InlineData(SqlFailureKind.Internal, "INTERNAL_ERROR", 65536, "GENERIC_INTERNAL_ERROR")]
    public async Task ErrorTable_MapsEveryKind_ToStandardTrinoCode(
        SqlFailureKind kind, string expectedType, int expectedCode, string expectedName)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var failure = new SqlFailure(kind, "Fixed test message", expectedName);
        var status = new StatementExecutionStatus("stmt-err-" + kind, "FAILED", null, null, 10, Failure: failure);

        await TrinoQueryResultsWriter.WriteAsync(context, status, token: 0);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FAILED");
        var error = root.GetProperty("error");
        error.GetProperty("errorType").GetString().ShouldBe(expectedType);
        error.GetProperty("errorCode").GetInt32().ShouldBe(expectedCode);
        error.GetProperty("errorName").GetString().ShouldBe(expectedName);
        error.GetProperty("message").GetString().ShouldBe("Fixed test message");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Failed_Error_ContainsNoSqlTableOrExceptionText()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var failure = SqlFailureClassifier.Classify(
            new InvalidOperationException("Internal leak: secret_table with password SECRET_PASS"));
        var status = new StatementExecutionStatus("stmt-sec", "FAILED", null, null, 10, Failure: failure);

        await TrinoQueryResultsWriter.WriteAsync(context, status, token: 0);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        var raw = root.GetProperty("error").GetRawText();
        raw.ShouldNotContain("secret_table");
        raw.ShouldNotContain("SECRET_PASS");
        raw.ShouldNotContain("InvalidOperationException");
        raw.ShouldNotContain("Internal leak");
        root.GetProperty("error").GetProperty("message").GetString().ShouldBe(SqlFailureClassifier.GenericServerErrorMessage);
    }

    [Fact]
    public async Task Truncated_AddsRowLimitWarning_OnLastPage()
    {
        // 5000 rows with Truncated = true => 2 pages
        var rows = Enumerable.Range(0, 5000)
            .Select(i => (IReadOnlyList<object?>)new object?[] { i })
            .ToList();
        var status = new StatementExecutionStatus("stmt-trunc", "FINISHED", ["id"], rows, 20, Truncated: true);

        // Page 0: not last page -> no warnings
        var ctx0 = new DefaultHttpContext();
        ctx0.Response.Body = new MemoryStream();
        await TrinoQueryResultsWriter.WriteAsync(ctx0, status, token: 0);

        ctx0.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc0 = await JsonDocument.ParseAsync(ctx0.Response.Body);
        doc0.RootElement.TryGetProperty("warnings", out _).ShouldBeFalse();

        // Page 1: last page -> warnings present
        var ctx1 = new DefaultHttpContext();
        ctx1.Response.Body = new MemoryStream();
        await TrinoQueryResultsWriter.WriteAsync(ctx1, status, token: 1);

        ctx1.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc1 = await JsonDocument.ParseAsync(ctx1.Response.Body);
        doc1.RootElement.TryGetProperty("warnings", out var warnings).ShouldBeTrue();
        warnings.GetArrayLength().ShouldBe(1);
        var warn = warnings[0];
        warn.GetProperty("warningCode").GetProperty("code").GetInt32().ShouldBe(0x0F00_0001);
        warn.GetProperty("warningCode").GetProperty("name").GetString().ShouldBe("AUTHERIS_ROW_LIMIT");
        warn.GetProperty("message").GetString().ShouldBe("The result was truncated at the gateway row limit.");
    }

    [Fact]
    public async Task Stats_ContainsAllClientRequiredFields()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var status = new StatementExecutionStatus("stmt-stats", "FINISHED", ["id"], [[1]], 42);

        await TrinoQueryResultsWriter.WriteAsync(context, status, token: 0);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var stats = doc.RootElement.GetProperty("stats");

        stats.GetProperty("state").GetString().ShouldBe("FINISHED");
        stats.GetProperty("queued").GetBoolean().ShouldBeFalse();
        stats.GetProperty("scheduled").GetBoolean().ShouldBeTrue();
        stats.GetProperty("nodes").GetInt32().ShouldBe(1);
        stats.GetProperty("totalSplits").GetInt32().ShouldBe(1);
        stats.GetProperty("queuedSplits").GetInt32().ShouldBe(0);
        stats.GetProperty("runningSplits").GetInt32().ShouldBe(0);
        stats.GetProperty("completedSplits").GetInt32().ShouldBe(1);
        stats.GetProperty("cpuTimeMillis").GetInt64().ShouldBe(42);
        stats.GetProperty("wallTimeMillis").GetInt64().ShouldBe(42);
        stats.GetProperty("queuedTimeMillis").GetInt64().ShouldBe(0);
        stats.GetProperty("elapsedTimeMillis").GetInt64().ShouldBe(42);
        stats.GetProperty("processedRows").GetInt64().ShouldBe(1);
        stats.GetProperty("processedBytes").GetInt64().ShouldBe(0);
        stats.GetProperty("physicalInputBytes").GetInt64().ShouldBe(0);
        stats.GetProperty("peakMemoryBytes").GetInt64().ShouldBe(0);
        stats.GetProperty("spilledBytes").GetInt64().ShouldBe(0);
    }
}
