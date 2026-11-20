namespace Autheris.Tests.Unit.Api;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Domain.Model;
using Autheris.Application.Catalog.Semantic;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class WebhookPreAuthBufferingTests
{
    private const string TestWebhookSecret = "preauth-buffering-test-secret-0123456789";

    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool WasRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WasRead = true;
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }

    private sealed class FakeOmWebhookHandler : IOpenMetadataWebhookHandler
    {
        public bool HandleCalled { get; private set; }
        public OpenMetadataWebhookOutcome Outcome { get; set; } = OpenMetadataWebhookOutcome.Processed;

        public Task<OpenMetadataWebhookOutcome> HandleVerifiedAsync(string payload, CancellationToken ct)
        {
            HandleCalled = true;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeDbtWebhookReceiver : IDbtCloudWebhookHandler
    {
        public bool ProcessCalled { get; private set; }
        public DbtWebhookProcessingResult Result { get; set; } = new(true, "Processed", "job_run.completed", 1L);

        public Task<DbtWebhookProcessingResult> HandleVerifiedAsync(string payload, CancellationToken ct)
        {
            ProcessCalled = true;
            return Task.FromResult(Result);
        }
    }

    private static string SignPayload(string payload) =>
        "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestWebhookSecret), Encoding.UTF8.GetBytes(payload)));

    private static DefaultHttpContext CreateOmContext(FakeOmWebhookHandler handler)
    {
        var services = new ServiceCollection();
        services.AddScoped<IOpenMetadataWebhookHandler>(_ => handler);
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions { WebhookSecret = TestWebhookSecret }
        }));
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider().CreateScope().ServiceProvider };
    }

    private static DefaultHttpContext CreateDbtContext(FakeDbtWebhookReceiver handler)
    {
        var services = new ServiceCollection();
        services.AddScoped<IDbtCloudWebhookHandler>(_ => handler);
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = TestWebhookSecret }
        }));
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider().CreateScope().ServiceProvider };
    }

    [Fact]
    public async Task OpenMetadata_MissingSignature_RejectsBeforeBufferingBody()
    {
        var handler = new FakeOmWebhookHandler();
        var context = CreateOmContext(handler);
        var stream = new TrackingStream(Encoding.UTF8.GetBytes("{\"test\":\"data\"}"));
        context.Request.Body = stream;
        context.Request.ContentLength = stream.Length;

        var result = await WebhookEndpoints.HandleOpenMetadataWebhookAsync(context);

        var problem = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        problem.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        stream.WasRead.ShouldBeFalse();
        handler.HandleCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task OpenMetadata_BodyExceeds2Mb_ReturnsPayloadTooLarge()
    {
        var handler = new FakeOmWebhookHandler();
        var context = CreateOmContext(handler);
        context.Request.Headers["X-OpenMetadata-Signature"] = "sha256=abcdef";
        var oversized = new byte[2 * 1024 * 1024 + 1024];
        var stream = new TrackingStream(oversized);
        context.Request.Body = stream;

        var result = await WebhookEndpoints.HandleOpenMetadataWebhookAsync(context);

        var problem = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        problem.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        handler.HandleCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Dbt_MissingSignature_RejectsBeforeBufferingBody()
    {
        var receiver = new FakeDbtWebhookReceiver();
        var context = CreateDbtContext(receiver);
        var stream = new TrackingStream(Encoding.UTF8.GetBytes("{\"test\":\"data\"}"));
        context.Request.Body = stream;
        context.Request.ContentLength = stream.Length;

        var result = await WebhookEndpoints.HandleDbtCloudWebhookAsync(context);

        var jsonResult = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        jsonResult.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        stream.WasRead.ShouldBeFalse();
        receiver.ProcessCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Dbt_BodyExceeds2Mb_ReturnsPayloadTooLarge()
    {
        var receiver = new FakeDbtWebhookReceiver();
        var context = CreateDbtContext(receiver);
        context.Request.Headers["X-Dbt-Signature"] = "sha256=abcdef";
        var oversized = new byte[2 * 1024 * 1024 + 1024];
        var stream = new TrackingStream(oversized);
        context.Request.Body = stream;

        var result = await WebhookEndpoints.HandleDbtCloudWebhookAsync(context);

        var problem = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        problem.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        receiver.ProcessCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task OpenMetadata_StoreUnavailable_ReturnsServiceUnavailable()
    {
        var handler = new FakeOmWebhookHandler { Outcome = OpenMetadataWebhookOutcome.Unavailable };
        var context = CreateOmContext(handler);
        const string payload = "{\"id\":\"00000000-0000-0000-0000-000000000001\"}";
        context.Request.Headers["X-OpenMetadata-Signature"] = SignPayload(payload);
        var bodyBytes = Encoding.UTF8.GetBytes(payload);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;

        var result = await WebhookEndpoints.HandleOpenMetadataWebhookAsync(context);

        var status = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        status.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult>()!;
        var details = problem.Value.ShouldBeOfType<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        details.Detail.ShouldBe("The webhook replay store is unavailable.");
        handler.HandleCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenMetadata_Rejected_ReturnsBadRequest()
    {
        var handler = new FakeOmWebhookHandler { Outcome = OpenMetadataWebhookOutcome.Rejected };
        var context = CreateOmContext(handler);
        const string payload = "{\"id\":\"00000000-0000-0000-0000-000000000001\"}";
        context.Request.Headers["X-OpenMetadata-Signature"] = SignPayload(payload);
        var bodyBytes = Encoding.UTF8.GetBytes(payload);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;

        var result = await WebhookEndpoints.HandleOpenMetadataWebhookAsync(context);

        var status = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        status.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        handler.HandleCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task Dbt_StoreUnavailable_ReturnsServiceUnavailable()
    {
        var receiver = new FakeDbtWebhookReceiver
        {
            Result = new DbtWebhookProcessingResult(false, "Webhook deduplication unavailable.", StoreUnavailable: true)
        };
        var context = CreateDbtContext(receiver);
        const string payload = "{\"eventId\":\"evt_1\"}";
        context.Request.Headers["X-Dbt-Signature"] = SignPayload(payload);
        var bodyBytes = Encoding.UTF8.GetBytes(payload);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;

        var result = await WebhookEndpoints.HandleDbtCloudWebhookAsync(context);

        var status = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        status.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        var valueResult = result.ShouldBeAssignableTo<IValueHttpResult>()!;
        var payloadResult = valueResult.Value.ShouldBeOfType<DbtWebhookProcessingResult>();
        payloadResult.Success.ShouldBeFalse();
        payloadResult.StoreUnavailable.ShouldBeTrue();
        payloadResult.Message.ShouldBe("Webhook deduplication unavailable.");
        receiver.ProcessCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task Dbt_Rejected_ReturnsUnauthorized()
    {
        var receiver = new FakeDbtWebhookReceiver
        {
            Result = new DbtWebhookProcessingResult(false, "Invalid or missing HMAC signature.")
        };
        var context = CreateDbtContext(receiver);
        const string payload = "{\"eventId\":\"evt_1\"}";
        context.Request.Headers["X-Dbt-Signature"] = SignPayload(payload);
        var bodyBytes = Encoding.UTF8.GetBytes(payload);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;

        var result = await WebhookEndpoints.HandleDbtCloudWebhookAsync(context);

        var status = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        status.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        receiver.ProcessCalled.ShouldBeTrue();
    }
}
