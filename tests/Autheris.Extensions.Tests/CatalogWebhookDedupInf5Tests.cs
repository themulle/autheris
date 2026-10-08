namespace Autheris.Extensions.Tests;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

/// <summary>
/// INF-5: catalog webhook deduplication works across gateway instances, and an event whose processing failed is not
/// swallowed as a duplicate when the provider retries it.
/// </summary>
[Collection("CatalogWebhookDedup")]
public sealed class CatalogWebhookDedupInf5Tests
{
    private const string Secret = "super-secret-catalog-key";

    private static (string Payload, string Signature, DateTimeOffset Timestamp) Signed(string eventId)
    {
        var payload = $$"""{"id":"{{eventId}}","eventType":"entityUpdated","entityType":"table","entityFullyQualifiedName":"sales_dw.public.orders"}""";
        var timestamp = DateTimeOffset.UtcNow;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var signature = "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp.ToUnixTimeSeconds()}.{payload}")));
        return (payload, signature, timestamp);
    }

    private static CatalogWebhookHandler Handler(IPolicyEpochRepository epochRepo, IDistributedClusterStateProvider? cluster = null) =>
        new(Options.Create(new GatewayOptions { OpenMetadata = new OpenMetadataOptions { WebhookSecret = Secret } }),
            epochRepo, Substitute.For<IDataCatalogSyncService>(), NullLogger<CatalogWebhookHandler>.Instance, cluster);

    [Fact]
    public async Task EventSeenByAnotherInstance_IsIgnored()
    {
        CatalogWebhookHandler.ResetDeduplicationCache();
        var cluster = Substitute.For<IDistributedClusterStateProvider>();
        cluster.IncrementAsync(Arg.Any<string>(), 1, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<long?>(2));
        var (payload, signature, ts) = Signed("evt-" + Guid.NewGuid());

        var result = await Handler(Substitute.For<IPolicyEpochRepository>(), cluster).HandleWebhookAsync(payload, signature, ts);

        result.Status.ShouldBe("IGNORED_DUPLICATE_EVENT");
    }

    [Fact]
    public async Task FailedProcessing_ReleasesTheEvent_SoTheRetryIsProcessed()
    {
        CatalogWebhookHandler.ResetDeduplicationCache();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();
        epochRepo.IncrementTableEpochAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<long>(new InvalidOperationException("db down")), Task.FromResult(2L));
        var handler = Handler(epochRepo);
        var (payload, signature, ts) = Signed("evt-" + Guid.NewGuid());

        var first = await handler.HandleWebhookAsync(payload, signature, ts);
        var retry = await handler.HandleWebhookAsync(payload, signature, ts);

        first.Success.ShouldBeFalse();
        first.Status.ShouldBe("PARTIALLY_FAILED");
        retry.Status.ShouldBe("INVALIDATED");
    }
}
