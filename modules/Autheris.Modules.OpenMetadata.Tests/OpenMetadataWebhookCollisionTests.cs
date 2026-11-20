using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Autheris.Extensions.Tests.TestSupport;
using Autheris.Modules.OpenMetadata;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Modules.OpenMetadata.Tests;

/// <summary>EXT-16: the webhook path applies the full sync's database-collision rejection.</summary>
[Trait("Category", "Security")]
public sealed class OpenMetadataWebhookCollisionTests
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
        var db1 = Table("db1");
        var db2 = Table("db2");
        var client = new StubOpenMetadataClient { Tables = [db1, db2] };
        var metadataRepo = new InMemoryTableMetadataRepository();
        var epochRepo = new InMemoryPolicyEpochRepository();

        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "test-hmac-key" },
            OpenMetadata = new OpenMetadataOptions { DefaultTenantId = "default-tenant", Enabled = true, WebhookSecret = Secret }
        };
        var service = new OpenMetadataSyncService(
            client, metadataRepo, new RecordingConsentRepository(), epochRepo,
            Options.Create(options), NullLogger<OpenMetadataSyncService>.Instance,
            clusterState: new InMemoryClusterStateProvider());

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = $"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":{nowMs},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"{db2.FullyQualifiedName}\"}}";

        var result = await service.HandleVerifiedAsync(payload);

        result.ShouldBe(OpenMetadataWebhookOutcome.Processed);
        metadataRepo.UpsertCalls.ShouldBe(0);
        epochRepo.IncrementCalls.ShouldBe(0);
    }
}
