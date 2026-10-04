namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class PostgreSqlLogicalCdcTests
{
    [Fact]
    public void WalMessageDecoder_DecodesInsert_ExtractsColumnsAndTenant()
    {
        // Arrange
        var decoder = new WalMessageDecoder();
        decoder.RegisterRelation(101, "public", "orders", ["id", "amount", "tenant_id"]);

        var afterRow = new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["amount"] = 99.99m,
            ["tenant_id"] = "tenant-emea"
        };

        var change = new WalChange(
            RelationId: 101,
            Operation: CdcOperation.Insert,
            Before: null,
            After: afterRow,
            Lsn: 100500,
            Timestamp: DateTimeOffset.UtcNow
        );

        // Act
        var cdcEvent = decoder.DecodeChange(change);

        // Assert
        cdcEvent.ShouldNotBeNull();
        cdcEvent.Operation.ShouldBe(CdcOperation.Insert);
        cdcEvent.Table.Schema.ShouldBe("public");
        cdcEvent.Table.TableName.ShouldBe("orders");
        cdcEvent.TenantId.ShouldBe("tenant-emea");
        cdcEvent.After.ShouldNotBeNull();
        cdcEvent.After["amount"].ShouldBe(99.99m);
        cdcEvent.Metadata.ShouldNotBeNull();
        cdcEvent.Metadata["lsn"].ShouldBe("100500");
    }

    [Fact]
    public void WalMessageDecoder_DecodesUpdate_PreservesBeforeAndAfter()
    {
        // Arrange
        var decoder = new WalMessageDecoder();
        decoder.RegisterRelation(102, "finance", "invoices", ["id", "status", "tenant_id"]);

        var beforeRow = new Dictionary<string, object?>
        {
            ["id"] = 42,
            ["status"] = "pending",
            ["tenant_id"] = "tenant-us"
        };

        var afterRow = new Dictionary<string, object?>
        {
            ["id"] = 42,
            ["status"] = "paid",
            ["tenant_id"] = "tenant-us"
        };

        var change = new WalChange(
            RelationId: 102,
            Operation: CdcOperation.Update,
            Before: beforeRow,
            After: afterRow,
            Lsn: 100600,
            Timestamp: DateTimeOffset.UtcNow
        );

        // Act
        var cdcEvent = decoder.DecodeChange(change);

        // Assert
        cdcEvent.ShouldNotBeNull();
        cdcEvent.Operation.ShouldBe(CdcOperation.Update);
        cdcEvent.Table.Schema.ShouldBe("finance");
        cdcEvent.Table.TableName.ShouldBe("invoices");
        cdcEvent.TenantId.ShouldBe("tenant-us");
        cdcEvent.Before!["status"].ShouldBe("pending");
        cdcEvent.After!["status"].ShouldBe("paid");
    }

    [Fact]
    public void WalMessageDecoder_DecodesDelete_PreservesBeforeAndTenant()
    {
        // Arrange
        var decoder = new WalMessageDecoder();
        decoder.RegisterRelation(103, "crm", "contacts", ["id", "email", "tenant"]);

        var beforeRow = new Dictionary<string, object?>
        {
            ["id"] = 77,
            ["email"] = "lead@example.com",
            ["tenant"] = "tenant-apac"
        };

        var change = new WalChange(
            RelationId: 103,
            Operation: CdcOperation.Delete,
            Before: beforeRow,
            After: null,
            Lsn: 100700,
            Timestamp: DateTimeOffset.UtcNow
        );

        // Act
        var cdcEvent = decoder.DecodeChange(change);

        // Assert
        cdcEvent.ShouldNotBeNull();
        cdcEvent.Operation.ShouldBe(CdcOperation.Delete);
        cdcEvent.TenantId.ShouldBe("tenant-apac");
        cdcEvent.Before!["email"].ShouldBe("lead@example.com");
    }

    [Fact]
    public async Task PostgreSqlLogicalReplicationService_PublishesDecodedEvents_ToCdcChannel()
    {
        // Arrange
        var channel = Substitute.For<ICdcEventChannel>();
        var options = new GatewayOptions
        {
            PostgreSqlCdc = new PostgreSqlCdcOptions
            {
                Enabled = true,
                MaxLagBytes = 1_000_000_000L
            }
        };
        var optionsWrapper = Options.Create(options);

        var service = new PostgreSqlLogicalReplicationService(
            channel,
            optionsWrapper,
            NullLogger<PostgreSqlLogicalReplicationService>.Instance);

        service.Decoder.RegisterRelation(200, "public", "accounts", ["id", "tenant_id"]);

        var change = new WalChange(
            RelationId: 200,
            Operation: CdcOperation.Insert,
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 10, ["tenant_id"] = "t1" },
            Lsn: 5000,
            Timestamp: DateTimeOffset.UtcNow
        );

        // Act
        await service.ProcessChangeAsync(change, serverLsn: 5100, CancellationToken.None);

        // Assert
        await channel.Received(1).PublishAsync(
            Arg.Is<CdcEvent>(e => e.Table.TableName == "accounts" && e.TenantId == "t1"),
            Arg.Any<CancellationToken>());

        service.LastAcknowledgedLsn.ShouldBe((ulong)5000);
        service.CurrentWalLagBytes.ShouldBe(100);
        service.IsWalLagExceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task PostgreSqlLogicalReplicationService_WalLagExceeded_ThrottlesAndWarns()
    {
        // Arrange: MaxLag 500 bytes, but server is at 10,000 and client at 0
        var channel = Substitute.For<ICdcEventChannel>();
        var options = new GatewayOptions
        {
            PostgreSqlCdc = new PostgreSqlCdcOptions
            {
                Enabled = true,
                MaxLagBytes = 500 // 500 bytes threshold
            }
        };
        var optionsWrapper = Options.Create(options);

        var service = new PostgreSqlLogicalReplicationService(
            channel,
            optionsWrapper,
            NullLogger<PostgreSqlLogicalReplicationService>.Instance);

        service.Decoder.RegisterRelation(201, "public", "events", ["id"]);

        var change = new WalChange(
            RelationId: 201,
            Operation: CdcOperation.Insert,
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 1 },
            Lsn: 100,
            Timestamp: DateTimeOffset.UtcNow
        );

        // Act: server LSN is 2000, which is > 500 bytes ahead
        await service.ProcessChangeAsync(change, serverLsn: 2000, CancellationToken.None);

        // Assert: Event should NOT be published, lag protection engaged
        await channel.DidNotReceive().PublishAsync(Arg.Any<CdcEvent>(), Arg.Any<CancellationToken>());
        service.IsWalLagExceeded.ShouldBeTrue();
        service.CurrentWalLagBytes.ShouldBe(2000);
    }

    [Fact]
    public async Task PostgreSqlLogicalReplicationService_FiltersTables_WhenTrackedTablesConfigured()
    {
        // Arrange
        var channel = Substitute.For<ICdcEventChannel>();
        var options = new GatewayOptions
        {
            PostgreSqlCdc = new PostgreSqlCdcOptions
            {
                Enabled = true,
                TrackedTables = ["public.allowed_table"]
            }
        };
        var optionsWrapper = Options.Create(options);

        var service = new PostgreSqlLogicalReplicationService(
            channel,
            optionsWrapper,
            NullLogger<PostgreSqlLogicalReplicationService>.Instance);

        service.Decoder.RegisterRelation(301, "public", "ignored_table", ["id"]);
        service.Decoder.RegisterRelation(302, "public", "allowed_table", ["id"]);

        var changeIgnored = new WalChange(301, CdcOperation.Insert, null, new Dictionary<string, object?> { ["id"] = 1 }, 10, DateTimeOffset.UtcNow);
        var changeAllowed = new WalChange(302, CdcOperation.Insert, null, new Dictionary<string, object?> { ["id"] = 2 }, 20, DateTimeOffset.UtcNow);

        // Act
        await service.ProcessChangeAsync(changeIgnored, 10, CancellationToken.None);
        await service.ProcessChangeAsync(changeAllowed, 20, CancellationToken.None);

        // Assert: only allowed_table published
        await channel.DidNotReceive().PublishAsync(Arg.Is<CdcEvent>(e => e.Table.TableName == "ignored_table"), Arg.Any<CancellationToken>());
        await channel.Received(1).PublishAsync(Arg.Is<CdcEvent>(e => e.Table.TableName == "allowed_table"), Arg.Any<CancellationToken>());
    }
}
