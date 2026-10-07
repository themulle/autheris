namespace Autheris.Tests.Unit.Performance;

using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Performance.IncrementalDelivery;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class IncrementalDeliveryTests
{
    private readonly GatewayOptions _options;

    public IncrementalDeliveryTests()
    {
        _options = new GatewayOptions
        {
            IncrementalDelivery = new IncrementalDeliveryOptions
            {
                Enabled = true,
                MaxDeferredExecutionTimeMs = 500, // 500ms for fast unit test
                MaxConcurrentStreamsPerClient = 2,
                MaxIncrementalChunks = 5
            }
        };
    }

    [Fact]
    public async Task SEC_PERF_04_IncrementalDeliveryFormatter_FormatsMultipartChunk_WithStrictBoundary()
    {
        // Arrange
        var formatter = new IncrementalDeliveryFormatter();
        using var stream = new MemoryStream();

        // Act
        await formatter.WriteInitialChunkAsync(stream, "{\"data\":{\"hero\":\"R2-D2\"},\"hasNext\":true}", hasNext: true);
        await formatter.WriteIncrementalChunkAsync(stream, "{\"hasNext\":false,\"incremental\":[{\"data\":{\"name\":\"Luke\"}}]}", hasNext: false);
        await formatter.WriteFinalBoundaryAsync(stream);

        // Assert
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        formatter.Boundary.ShouldBe("-");
        formatter.ContentType.ShouldBe("multipart/mixed; boundary=\"-\"");
        content.ShouldContain("\r\n---\r\nContent-Type: application/json; charset=utf-8\r\n\r\n{\"data\":{\"hero\":\"R2-D2\"},\"hasNext\":true}");
        content.ShouldContain("\r\n---\r\nContent-Type: application/json; charset=utf-8\r\n\r\n{\"hasNext\":false,\"incremental\":[{\"data\":{\"name\":\"Luke\"}}]}");
        content.ShouldEndWith("\r\n-----\r\n");
    }

    [Fact]
    public void SEC_PERF_01_IncrementalDelivery_AbortsBackgroundProcessing_WhenClientCancelsToken()
    {
        // Arrange
        var manager = new IncrementalDeliveryManager(Options.Create(_options), NullLogger<IncrementalDeliveryManager>.Instance);
        using var clientCts = new CancellationTokenSource();

        // Act
        using var streamCts = manager.CreateStreamTimeoutCts(clientCts.Token);
        clientCts.Cancel(); // Client disconnects

        // Assert
        streamCts.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task SEC_PERF_02_IncrementalDelivery_EnforcesMaxExecutionTimeout_AndCancels()
    {
        // Arrange
        var manager = new IncrementalDeliveryManager(Options.Create(_options), NullLogger<IncrementalDeliveryManager>.Instance);
        using var clientCts = new CancellationTokenSource();

        // Act
        using var streamCts = manager.CreateStreamTimeoutCts(clientCts.Token);
        streamCts.Token.IsCancellationRequested.ShouldBeFalse();

        // Wait for timeout (options configured with 500ms)
        await Task.Delay(800);

        // Assert
        streamCts.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void SEC_PERF_03_IncrementalDeliveryManager_EnforcesMaxConcurrentStreams_PerClient()
    {
        // Arrange
        var manager = new IncrementalDeliveryManager(Options.Create(_options), NullLogger<IncrementalDeliveryManager>.Instance);
        var clientKey = "tenant-fast-client";

        // Act & Assert (MaxConcurrentStreamsPerClient = 2)
        manager.TryAcquireStreamSlot(clientKey).ShouldBeTrue();
        manager.TryAcquireStreamSlot(clientKey).ShouldBeTrue();
        manager.TryAcquireStreamSlot(clientKey).ShouldBeFalse(); // 3rd stream must be blocked

        // Release one slot
        manager.ReleaseStreamSlot(clientKey);
        manager.TryAcquireStreamSlot(clientKey).ShouldBeTrue(); // Slot is free again
    }

    [Fact]
    public async Task IncrementalDeliveryMiddleware_RejectsRequest_With429_WhenStreamLimitExceeded()
    {
        // Arrange
        var manager = new IncrementalDeliveryManager(Options.Create(_options), NullLogger<IncrementalDeliveryManager>.Instance);
        var clientKey = "tenant-blocked";

        // Saturate active stream slots
        manager.TryAcquireStreamSlot(clientKey).ShouldBeTrue();
        manager.TryAcquireStreamSlot(clientKey).ShouldBeTrue();

        var middleware = new IncrementalDeliveryMiddleware(
            next: (ctx) => Task.CompletedTask,
            NullLogger<IncrementalDeliveryMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = "multipart/mixed; boundary=\"-\"";
        context.Response.Body = new MemoryStream();

        var claims = new[] { new Claim("tenant_id", clientKey) };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        // Act
        await middleware.InvokeAsync(context, manager);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        body.ShouldContain("INCREMENTAL_STREAM_LIMIT_EXCEEDED");
    }
}
