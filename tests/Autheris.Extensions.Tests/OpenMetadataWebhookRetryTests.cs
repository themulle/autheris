using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Autheris.Extensions.Tests.TestSupport;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Extensions.Tests;

/// <summary>EXT-12: a failed webhook event is released from deduplication so the provider's retry is processed.</summary>
public sealed class OpenMetadataWebhookRetryTests
{
    private const string Secret = "webhook-secret-123";

    [Fact]
    public async Task FailedEvent_RetryWithSameId_IsProcessedAgain()
    {
        var metadataRepo = new InMemoryTableMetadataRepository();
        var epochRepo = new InMemoryPolicyEpochRepository();
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "test-hmac-key" },
            OpenMetadata = new OpenMetadataOptions { DefaultTenantId = "default-tenant", Enabled = true, WebhookSecret = Secret }
        };

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = $"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":{nowMs},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"service.db.schema.retry_t\"}}";
        var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(payload)));

        var table = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "retry_t",
            FullyQualifiedName = "service.db.schema.retry_t",
            Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
        };

        var calls = 0;
        var client = new StubOpenMetadataClient
        {
            TableByFqnResolver = fqn => fqn == "service.db.schema.retry_t" && ++calls == 1
                ? throw new HttpRequestException("transient")
                : fqn == "service.db.schema.retry_t" ? table : null
        };

        var service = new OpenMetadataSyncService(
            client, metadataRepo, new RecordingConsentRepository(), epochRepo,
            Options.Create(options), NullLogger<OpenMetadataSyncService>.Instance,
            clusterState: new InMemoryClusterStateProvider());

        await Should.ThrowAsync<HttpRequestException>(() => service.HandleWebhookEventAsync(payload, signature));

        var retried = await service.HandleWebhookEventAsync(payload, signature);

        retried.ShouldBe(OpenMetadataWebhookOutcome.Processed);
        calls.ShouldBe(2);
        epochRepo.IncrementCalls.ShouldBe(1);
    }
}
