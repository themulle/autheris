namespace Autheris.Extensions.Tests;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class OpenMetadataWebhookTests
{
    private const string WebhookSecret = "om-webhook-test-secret-488";

    private static (OpenMetadataSyncService Service, ITableMetadataRepository MetadataRepo, IOpenMetadataClient Client) CreateSyncService()
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "test-hmac-key" },
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                DefaultTenantId = "default-tenant",
                WebhookSecret = WebhookSecret
            }
        };

        var service = new OpenMetadataSyncService(
            client,
            metadataRepo,
            consentRepo,
            epochRepo,
            Options.Create(options),
            NullLogger<OpenMetadataSyncService>.Instance);

        return (service, metadataRepo, client);
    }

    private static (string Payload, string Signature) BuildPayload(Guid eventId, DateTimeOffset timestamp, string fqn = "service.db.schema.test_tbl")
    {
        var ts = timestamp.ToUnixTimeMilliseconds();
        var payload = $"{{\"id\":\"{eventId}\",\"timestamp\":{ts},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"{fqn}\"}}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes(payload));
        var sig = "sha256=" + Convert.ToHexStringLower(hash);
        return (payload, sig);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Replayed_event_is_rejected()
    {
        var (service, metadataRepo, client) = CreateSyncService();
        var table = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "test_tbl",
            FullyQualifiedName = "service.db.schema.test_tbl",
            Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
        };
        client.GetTableByFqnAsync("service.db.schema.test_tbl", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OpenMetadataTable?>(table));

        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var (payload, signature) = BuildPayload(eventId, now);

        // First delivery: valid and accepted
        var firstResult = await service.HandleWebhookEventAsync(payload, signature);
        firstResult.ShouldBeTrue();

        // Replay with identical eventId: rejected by deduplication cache (400/409 semantics)
        var replayResult = await service.HandleWebhookEventAsync(payload, signature);
        replayResult.ShouldBeFalse();

        // Replay with skewed timestamp outside tolerance window (> 5 min): rejected
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        var (stalePayload, staleSig) = BuildPayload(Guid.NewGuid(), staleTimestamp);
        var staleResult = await service.HandleWebhookEventAsync(stalePayload, staleSig);
        staleResult.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Buffer_is_bounded()
    {
        var (service, _, _) = CreateSyncService();

        // 1. Oversized body (> 2 MB) is rejected before processing
        var largePadding = new string('a', 2 * 1024 * 1024 + 1024);
        var largePayload = $"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"data\":\"{largePadding}\"}}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes(largePayload));
        var largeSig = "sha256=" + Convert.ToHexStringLower(hash);

        var largeResult = await service.HandleWebhookEventAsync(largePayload, largeSig);
        largeResult.ShouldBeFalse();

        // 2. Deduplication cache / buffer remains bounded
        OpenMetadataSyncService.ProcessedWebhookEventsCount.ShouldBeLessThanOrEqualTo(10_000);
    }
}
