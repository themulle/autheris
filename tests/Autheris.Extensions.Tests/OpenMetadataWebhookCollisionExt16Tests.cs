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
using Shouldly;
using Xunit;

namespace Autheris.Extensions.Tests;

/// <summary>EXT-16: the webhook path applies the full sync's database-collision rejection.</summary>
[Trait("Category", "Security")]
public sealed class OpenMetadataWebhookCollisionExt16Tests
{
    private const string Secret = "webhook-secret-123";

    private static OpenMetadataTable Table(string db) => new()
    {
        Id = Guid.NewGuid(),
        Name = "customers",
        FullyQualifiedName = $"svc.{db}.public.customers",
        Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
    };

    [Fact]
    public async Task TableOfAnotherDatabaseWithTheSameIdentity_IsNotMergedByTheWebhook()
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();
        var db1 = Table("db1");
        var db2 = Table("db2");
        client.GetTableByFqnAsync(db2.FullyQualifiedName, Arg.Any<CancellationToken>()).Returns(Task.FromResult<OpenMetadataTable?>(db2));
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>([db1, db2]));

        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "test-hmac-key" },
            OpenMetadata = new OpenMetadataOptions { DefaultTenantId = "legacy-single-tenant", Enabled = true, WebhookSecret = Secret }
        };
        var service = new OpenMetadataSyncService(
            client, metadataRepo, Substitute.For<IConsentRepository>(), epochRepo,
            Options.Create(options), NullLogger<OpenMetadataSyncService>.Instance);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = $"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":{nowMs},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"{db2.FullyQualifiedName}\"}}";
        var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(payload)));

        var result = await service.HandleWebhookEventAsync(payload, signature);

        result.ShouldBeTrue();
        await metadataRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<Autheris.Domain.Model.TableMetadata>(), Arg.Any<CancellationToken>());
        await epochRepo.DidNotReceive().IncrementTableEpochAsync(Arg.Any<Autheris.Domain.Common.TableIdentifier>(), Arg.Any<CancellationToken>());
    }
}
