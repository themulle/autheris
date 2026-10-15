namespace Autheris.Tests.Unit;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (D-11, SEC-94-13): the WebSQL 504 is RFC 9457 problem details without SQL or exception data.</summary>
[Trait("Category", "Security")]
public sealed class WebSqlTimeoutProblemDetailsTests
{
    [Fact]
    public async Task Body_IsProblemJson_WithoutSqlOrExceptionData()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-504";
        var ex = new TimeoutException("SELECT secret FROM hr.salaries ORDER BY ssn -- Execution Timeout Expired at host db01");

        await WebSqlEndpoints.WriteWebSqlErrorAsync(context, ex, NullLogger.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status504GatewayTimeout);
        context.Response.ContentType.ShouldStartWith("application/problem+json");
        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().ShouldNotBeNullOrWhiteSpace();
        Uri.TryCreate(root.GetProperty("type").GetString(), UriKind.Absolute, out _).ShouldBeTrue();
        root.GetProperty("title").GetString().ShouldBe("Gateway Timeout");
        root.GetProperty("status").GetInt32().ShouldBe(504);
        root.GetProperty("detail").GetString().ShouldBe(WebSqlEndpoints.GenericTimeoutMessage);
        root.GetProperty("traceId").GetString().ShouldBe("trace-504");
        root.TryGetProperty("instance", out _).ShouldBeFalse();
        raw.ShouldNotContain("SELECT", Case.Insensitive);
        raw.ShouldNotContain("salaries");
        raw.ShouldNotContain("db01");
        raw.ShouldNotContain("TimeoutException");
        raw.ShouldNotContain("StackTrace", Case.Insensitive);
    }
}
