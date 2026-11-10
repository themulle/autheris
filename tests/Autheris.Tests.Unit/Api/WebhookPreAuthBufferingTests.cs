namespace Autheris.Tests.Unit.Api;

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class WebhookPreAuthBufferingTests
{
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

    private sealed class FakeOmSyncService : IOpenMetadataSyncService
    {
        public bool HandleCalled { get; private set; }
        public Task<OpenMetadataSyncResult> SyncPermissionsAsync(bool dryRun = false, TenantId? restrictToTenant = null, CancellationToken ct = default) =>
            Task.FromResult(new OpenMetadataSyncResult(0, 0, 0, [], [], true));
        public Task<bool> HandleWebhookEventAsync(string eventPayload, string? signatureHeader = null, CancellationToken ct = default)
        {
            HandleCalled = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDbtWebhookReceiver : IDbtWebhookReceiver
    {
        public bool ProcessCalled { get; private set; }
        public bool ValidateSignature(string payload, string? signatureHeader, string secret) => true;
        public Task<DbtWebhookProcessingResult> ProcessWebhookAsync(string payload, string? signatureHeader, CancellationToken ct = default)
        {
            ProcessCalled = true;
            return Task.FromResult(new DbtWebhookProcessingResult(true, "Processed", "job_run.completed", 1L));
        }
    }

    [Fact]
    public async Task OpenMetadata_MissingSignature_RejectsBeforeBufferingBody()
    {
        var syncService = new FakeOmSyncService();
        var context = new DefaultHttpContext();
        var stream = new TrackingStream(Encoding.UTF8.GetBytes("{\"test\":\"data\"}"));
        context.Request.Body = stream;
        context.Request.ContentLength = stream.Length;

        var result = await WebhookEndpoints.HandleOpenMetadataWebhookAsync(context, syncService);

        var problem = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        stream.WasRead.ShouldBeFalse();
        syncService.HandleCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task OpenMetadata_BodyExceeds2Mb_ReturnsPayloadTooLarge()
    {
        var syncService = new FakeOmSyncService();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-OpenMetadata-Signature"] = "sha256=abcdef";
        var oversized = new byte[2 * 1024 * 1024 + 1024];
        var stream = new TrackingStream(oversized);
        context.Request.Body = stream;

        var result = await WebhookEndpoints.HandleOpenMetadataWebhookAsync(context, syncService);

        var problem = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        problem.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        syncService.HandleCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Dbt_MissingSignature_RejectsBeforeBufferingBody()
    {
        var receiver = new FakeDbtWebhookReceiver();
        var context = new DefaultHttpContext();
        var stream = new TrackingStream(Encoding.UTF8.GetBytes("{\"test\":\"data\"}"));
        context.Request.Body = stream;
        context.Request.ContentLength = stream.Length;

        var result = await DbtEndpoints.HandleDbtCloudWebhookAsync(context, receiver);

        var jsonResult = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        jsonResult.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        stream.WasRead.ShouldBeFalse();
        receiver.ProcessCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Dbt_BodyExceeds2Mb_ReturnsPayloadTooLarge()
    {
        var receiver = new FakeDbtWebhookReceiver();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Dbt-Signature"] = "sha256=abcdef";
        var oversized = new byte[2 * 1024 * 1024 + 1024];
        var stream = new TrackingStream(oversized);
        context.Request.Body = stream;

        var result = await DbtEndpoints.HandleDbtCloudWebhookAsync(context, receiver);

        var problem = result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!;
        problem.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        receiver.ProcessCalled.ShouldBeFalse();
    }
}
