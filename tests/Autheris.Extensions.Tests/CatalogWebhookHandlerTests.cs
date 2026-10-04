namespace Autheris.Extensions.Tests;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CatalogWebhookHandlerTests
{
    private static string ComputeHmacSha256(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    [Fact]
    public async Task HandleWebhookAsync_OpenMetadataChange_ShouldValidateSignatureAndBumpEpoch()
    {
        // Arrange
        const string secret = "super-secret-catalog-key";
        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                WebhookSecret = secret
            }
        });

        var epochRepo = Substitute.For<IPolicyEpochRepository>();
        var syncService = Substitute.For<IDataCatalogSyncService>();

        var handler = new CatalogWebhookHandler(
            options,
            epochRepo,
            syncService,
            NullLogger<CatalogWebhookHandler>.Instance);

        var payload = """
        {
            "eventType": "entityUpdated",
            "entityType": "table",
            "entityFullyQualifiedName": "sales_dw.public.orders",
            "previousVersion": 1.0,
            "currentVersion": 1.1
        }
        """;

        var timestamp = DateTimeOffset.UtcNow;
        // SEC M-34: signature covers "{unixSeconds}.{payload}"
        var signature = "sha256=" + ComputeHmacSha256($"{timestamp.ToUnixTimeSeconds()}.{payload}", secret);

        // Act
        var result = await handler.HandleWebhookAsync(payload, signature, timestamp);

        // Assert
        result.Success.ShouldBeTrue();
        result.Status.ShouldBe("INVALIDATED");
        result.AffectedTables.Count.ShouldBe(1);
        result.AffectedTables[0].TableName.ShouldBe("orders");

        await epochRepo.Received(1).IncrementTableEpochAsync(
            Arg.Is<TableIdentifier>(t => t.TableName == "orders"),
            Arg.Any<CancellationToken>());

        await syncService.Received(1).EnrichOrReferenceTableAsync(
            Arg.Is<TableIdentifier>(t => t.TableName == "orders"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_InvalidSignature_ShouldRejectUnauthorized()
    {
        // Arrange
        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                WebhookSecret = "correct-secret"
            }
        });

        var epochRepo = Substitute.For<IPolicyEpochRepository>();
        var syncService = Substitute.For<IDataCatalogSyncService>();

        var handler = new CatalogWebhookHandler(
            options,
            epochRepo,
            syncService,
            NullLogger<CatalogWebhookHandler>.Instance);

        var payload = "{\"entityType\":\"table\"}";
        var invalidSignature = "sha256=0000000000000000000000000000000000000000000000000000000000000000";
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var result = await handler.HandleWebhookAsync(payload, invalidSignature, timestamp);

        // Assert
        result.Success.ShouldBeFalse();
        result.Status.ShouldBe("REJECTED_INVALID_SIGNATURE");
        await epochRepo.DidNotReceive().IncrementTableEpochAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_ExpiredTimestamp_ShouldRejectTimestampExpired()
    {
        // Arrange
        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                WebhookSecret = "secret"
            }
        });

        var epochRepo = Substitute.For<IPolicyEpochRepository>();
        var syncService = Substitute.For<IDataCatalogSyncService>();

        var handler = new CatalogWebhookHandler(
            options,
            epochRepo,
            syncService,
            NullLogger<CatalogWebhookHandler>.Instance);

        var payload = "{\"entityType\":\"table\"}";
        var signature = "sha256=123456";
        var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10); // 10 minutes ago (> 5 min tolerance)

        // Act
        var result = await handler.HandleWebhookAsync(payload, signature, expiredTimestamp);

        // Assert
        result.Success.ShouldBeFalse();
        result.Status.ShouldBe("REJECTED_TIMESTAMP_EXPIRED");
    }

    [Fact]
    public async Task HandleWebhookAsync_PurviewEvent_ShouldParseAndBumpEpoch()
    {
        // Arrange
        const string secret = "purview-secret";
        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                WebhookSecret = secret
            }
        });

        var epochRepo = Substitute.For<IPolicyEpochRepository>();
        var syncService = Substitute.For<IDataCatalogSyncService>();

        var handler = new CatalogWebhookHandler(
            options,
            epochRepo,
            syncService,
            NullLogger<CatalogWebhookHandler>.Instance);

        var payload = """
        [
            {
                "eventType": "Microsoft.Purview.ClassificationAdded",
                "data": {
                    "qualifiedName": "finance_db/accounts"
                }
            }
        ]
        """;

        var timestamp = DateTimeOffset.UtcNow;
        var signature = ComputeHmacSha256($"{timestamp.ToUnixTimeSeconds()}.{payload}", secret);

        // Act
        var result = await handler.HandleWebhookAsync(payload, signature, timestamp, provider: "Purview");

        // Assert
        result.Success.ShouldBeTrue();
        result.Status.ShouldBe("INVALIDATED");
        result.AffectedTables.Count.ShouldBe(1);
        result.AffectedTables[0].TableName.ShouldBe("accounts");

        await epochRepo.Received(1).IncrementTableEpochAsync(
            Arg.Is<TableIdentifier>(t => t.TableName == "accounts"),
            Arg.Any<CancellationToken>());
    }
}
