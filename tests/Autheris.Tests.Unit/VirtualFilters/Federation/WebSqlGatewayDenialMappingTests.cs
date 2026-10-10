namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 WI-15 (ST-35, HTTP half): a gateway denial (stable code) is HTTP 403 with the generic message on the WebSQL
/// endpoint; the code and the exception message never reach the response (they are in the audit record).
/// </summary>
public sealed class WebSqlGatewayDenialMappingTests
{
    [Theory]
    [InlineData("VF_SCAN_NOT_PERMITTED")]
    [InlineData("VIRTUAL_FILTER_SOURCE_UNAVAILABLE")]
    [InlineData("VF_PATTERN_EVALUATION_FAILED")]
    [InlineData("ROW_LIMIT_EXCEEDED")]
    [InlineData("TABLE_DENIED")]
    public async Task GatewaySecurityException_Returns403_WithTheGenericMessage(string code)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-403";

        await WebSqlEndpoints.WriteWebSqlErrorAsync(
            context, new GatewaySecurityException("secret-detail for filter vf_x", code), NullLogger.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("Forbidden");
        doc.RootElement.GetProperty("message").GetString().ShouldBe(WebSqlEndpoints.GenericForbiddenMessage);
        doc.RootElement.ToString().ShouldNotContain("secret-detail");
        doc.RootElement.ToString().ShouldNotContain(code);
    }

    private const string Sentinel = "SENTINEL-customer-4711-secret-value";

    /// <summary>Captures everything a logger receives, including the complete exception chain (type, message, stack).</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception) + " || " + exception + " || " + string.Join(";", state is IEnumerable<KeyValuePair<string, object?>> kv ? kv : []));
    }

    private static async Task<(int Status, string Body)> WriteAsync(Exception exception, ILogger logger)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-cr02";

        await WebSqlEndpoints.WriteWebSqlErrorAsync(context, exception, logger, CancellationToken.None);

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Theory]
    [InlineData("VIRTUAL_FILTER_SOURCE_UNAVAILABLE")]
    [InlineData("VF_SCAN_NOT_PERMITTED")]
    [InlineData("ROW_LIMIT_EXCEEDED")]
    public async Task GatewaySecurityException_LogsOnlyTheCodeAndTheType_NeverTheMessageOrTheInnerException(string code)
    {
        // CR-02 (C-8, SEC-11): the inner exception of a key source failure can quote data values.
        var logger = new CapturingLogger();
        var inner = new InvalidOperationException($"ORA-00001: unique constraint violated for value '{Sentinel}'");
        var exception = new GatewaySecurityException($"outer message {Sentinel}", code, inner);

        await WriteAsync(exception, logger);

        logger.Entries.ShouldNotBeEmpty();
        logger.Entries.ShouldAllBe(e => !e.Contains(Sentinel) && !e.Contains("ORA-00001") && !e.Contains("InvalidOperationException"));
        logger.Entries.ShouldContain(e => e.Contains(code) && e.Contains(nameof(GatewaySecurityException)));
    }

    public static IEnumerable<object[]> SecurityExceptions() =>
    [
        [new GatewaySecurityException("denied", "TABLE_DENIED")],
        [new GatewaySecurityException("denied", "VF_SCAN_NOT_PERMITTED", new InvalidOperationException("inner"))],
        [new GatewaySecurityException("too large", "RESPONSE_TOO_LARGE")],
        [new GatewayUnauthorizedException()],
        [new GatewayForbiddenException()]
    ];

    [Theory]
    [MemberData(nameof(SecurityExceptions))]
    public async Task GatewaySecurityException_StatusIsTheSameOnTheWebSqlEndpointAndTheGlobalHandler(GatewaySecurityException exception)
    {
        var (webSqlStatus, body) = await WriteAsync(exception, new CapturingLogger());

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = new GatewayExceptionHandler(Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayExceptionHandler>.Instance);
        (await handler.TryHandleAsync(context, exception, CancellationToken.None)).ShouldBeTrue();

        webSqlStatus.ShouldBe(context.Response.StatusCode);
        body.ShouldNotContain(exception.Message);
    }

    [Fact]
    public async Task Statuses_Are401ForUnauthorized_400ForTooLarge_403Otherwise()
    {
        (await WriteAsync(new GatewayUnauthorizedException(), new CapturingLogger())).Status.ShouldBe(StatusCodes.Status401Unauthorized);
        (await WriteAsync(new GatewaySecurityException("x", "RESPONSE_TOO_LARGE"), new CapturingLogger())).Status.ShouldBe(StatusCodes.Status400BadRequest);
        (await WriteAsync(new GatewayForbiddenException(), new CapturingLogger())).Status.ShouldBe(StatusCodes.Status403Forbidden);
    }
}
