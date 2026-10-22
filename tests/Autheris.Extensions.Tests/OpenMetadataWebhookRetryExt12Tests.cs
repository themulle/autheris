using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Autheris.Extensions.Tests;

/// <summary>EXT-12: a failed webhook event is released from deduplication so the provider's retry is processed.</summary>
public sealed class OpenMetadataWebhookRetryExt12Tests
{
    private const string Secret = "webhook-secret-123";

    [Fact]
    public async Task FailedEvent_RetryWithSameId_IsProcessedAgain()
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "test-hmac-key" },
            OpenMetadata = new OpenMetadataOptions { Enabled = true, WebhookSecret = Secret }
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
        client.GetTableByFqnAsync("service.db.schema.retry_t", Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? Task.FromException<OpenMetadataTable?>(new HttpRequestException("transient"))
                : Task.FromResult<OpenMetadataTable?>(table));

        var service = new OpenMetadataSyncService(
            client, metadataRepo, Substitute.For<IConsentRepository>(), epochRepo,
            Options.Create(options), NullLogger<OpenMetadataSyncService>.Instance);

        await Should.ThrowAsync<HttpRequestException>(() => service.HandleWebhookEventAsync(payload, signature));

        var retried = await service.HandleWebhookEventAsync(payload, signature);

        retried.ShouldBeTrue();
        calls.ShouldBe(2);
        await epochRepo.Received(1).IncrementTableEpochAsync(Arg.Any<Autheris.Domain.Common.TableIdentifier>(), Arg.Any<CancellationToken>());
    }
}
