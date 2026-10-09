namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class VirtualFilterWebhookTriggerTests : IDisposable
{
    private const string WebhookSecret = "test-gitops-webhook-secret-2026";
    private readonly SqliteGovernanceRepository _repository = new(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=vf_wh_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));

    private readonly VirtualFilterAdministrationService _service;
    private readonly IOptions<GatewayOptions> _options;

    public VirtualFilterWebhookTriggerTests()
    {
        _options = Options.Create(new GatewayOptions
        {
            VirtualFilters = new VirtualFilterOptions
            {
                WebhookSecret = WebhookSecret,
                GitRef = "refs/heads/main"
            }
        });
        _service = new VirtualFilterAdministrationService(_repository, Substitute.For<IAuditLogRepository>(), _options);
    }

    public void Dispose() => _repository.Dispose();

    private static string ComputeHubSignature(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HttpContext CreateWebhookContext(string payload, string? deliveryId, string? signature)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        if (!string.IsNullOrWhiteSpace(deliveryId))
        {
            context.Request.Headers["X-GitHub-Delivery"] = deliveryId;
        }

        if (!string.IsNullOrWhiteSpace(signature))
        {
            context.Request.Headers["X-Hub-Signature-256"] = signature;
        }

        return context;
    }

    private static async Task<int> ExecuteResultAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task Webhook_MissingDeliveryHeader_ReturnsBadRequest400()
    {
        var payload = JsonSerializer.Serialize(new { @ref = "refs/heads/main" });
        var sig = ComputeHubSignature(payload, WebhookSecret);
        var context = CreateWebhookContext(payload, deliveryId: null, signature: sig);

        var result = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(context, _service, _options);
        var statusCode = await ExecuteResultAsync(result, context);

        statusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Webhook_MissingSignature_ReturnsUnauthorized401()
    {
        var payload = JsonSerializer.Serialize(new { @ref = "refs/heads/main" });
        var context = CreateWebhookContext(payload, deliveryId: Guid.NewGuid().ToString(), signature: null);

        var result = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(context, _service, _options);
        var statusCode = await ExecuteResultAsync(result, context);

        statusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Webhook_InvalidSignature_ReturnsUnauthorized401()
    {
        var payload = JsonSerializer.Serialize(new { @ref = "refs/heads/main" });
        var invalidSig = "sha256=" + new string('0', 64);
        var context = CreateWebhookContext(payload, deliveryId: Guid.NewGuid().ToString(), signature: invalidSig);

        var result = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(context, _service, _options);
        var statusCode = await ExecuteResultAsync(result, context);

        statusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Webhook_BypassOptionsEnabled_StillRejectsInvalidSignature()
    {
        var bypassOptions = Options.Create(new GatewayOptions
        {
            VirtualFilters = new VirtualFilterOptions
            {
                WebhookSecret = WebhookSecret,
                GitRef = "refs/heads/main"
            },
            Insecure = new InsecureGettingStartedOptions
            {
                danger_bypass_webhook_signature_validation = true
            }
        });

        var payload = JsonSerializer.Serialize(new { @ref = "refs/heads/main" });
        var invalidSig = "sha256=" + new string('f', 64);
        var context = CreateWebhookContext(payload, deliveryId: Guid.NewGuid().ToString(), signature: invalidSig);

        var result = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(context, _service, bypassOptions);
        var statusCode = await ExecuteResultAsync(result, context);

        statusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Webhook_ValidSignature_MismatchedRef_ReturnsIgnored()
    {
        var payload = JsonSerializer.Serialize(new { @ref = "refs/heads/feature-branch" });
        var sig = ComputeHubSignature(payload, WebhookSecret);
        var context = CreateWebhookContext(payload, deliveryId: Guid.NewGuid().ToString(), signature: sig);

        var result = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(context, _service, _options);
        var statusCode = await ExecuteResultAsync(result, context);

        statusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Ignored");
    }

    [Fact]
    public async Task Webhook_ValidSignatureAndMatchingRef_ReturnsAccepted202AndUpdatesStatus()
    {
        var deliveryId = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(new { @ref = "refs/heads/main" });
        var sig = ComputeHubSignature(payload, WebhookSecret);
        var context = CreateWebhookContext(payload, deliveryId, signature: sig);

        var result = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(context, _service, _options);
        var statusCode = await ExecuteResultAsync(result, context);

        statusCode.ShouldBe(StatusCodes.Status202Accepted);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Accepted");
        doc.RootElement.GetProperty("delivery").GetString().ShouldBe(deliveryId);

        _service.LastWebhookDeliveryId.ShouldBe(deliveryId);
        _service.LastWebhookTriggerAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Webhook_DuplicateDeliveryId_ReturnsAcceptedWithoutDoubleTrigger()
    {
        var deliveryId = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(new { @ref = "refs/heads/main" });
        var sig = ComputeHubSignature(payload, WebhookSecret);

        var context1 = CreateWebhookContext(payload, deliveryId, signature: sig);
        var result1 = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(context1, _service, _options);
        (await ExecuteResultAsync(result1, context1)).ShouldBe(StatusCodes.Status202Accepted);

        var initialTriggerTime = _service.LastWebhookTriggerAt;

        var context2 = CreateWebhookContext(payload, deliveryId, signature: sig);
        var result2 = await VirtualFilterEndpoints.HandleConfigSyncWebhookAsync(context2, _service, _options);
        (await ExecuteResultAsync(result2, context2)).ShouldBe(StatusCodes.Status202Accepted);

        _service.LastWebhookTriggerAt.ShouldBe(initialTriggerTime);
    }
}
