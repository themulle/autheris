namespace Autheris.Tests.Unit.Governance;

using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

public sealed class Pol9DegradedModeSensitivityTests
{
    [Fact]
    public async Task IsEpochValidAsync_WhenDegraded_EvaluatesTableMetadataSensitivityNotSubstring()
    {
        // POL-9: In degraded mode, sensitivity must be determined by TableMetadata.IsHighlySensitive
        // (per ADR-010: CONFIDENTIAL, RESTRICTED, SECRET, RequiresFourEyes), NOT by name substring matching.
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(false); // Disconnected -> degraded

        var tableRepo = Substitute.For<ITableMetadataRepository>();

        // Table A: Name has NO sensitive keywords, but Metadata is RESTRICTED (Highly Sensitive)
        var tableRestricted = new TableIdentifier("sales", "crm", "customer_data_export_42");
        tableRepo.GetTableMetadataAsync(tableRestricted, Arg.Any<CancellationToken>())
            .Returns(new TableMetadata
            {
                Identifier = tableRestricted,
                Table = new Table
                {
                    TableName = "customer_data_export_42",
                    SchemaName = "crm",
                    SourceName = "sales",
                    Sensitivity = "RESTRICTED"
                }
            });

        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                EpochValidation = new EpochValidationOptions
                {
                    FailClosedOnSensitiveTables = true
                }
            }
        });

        var service = new EpochValidationService(
            options,
            multiplexer: multiplexer,
            tableMetadataRepository: tableRepo);

        // Act: check validity for table with RESTRICTED sensitivity
        var isValid = await service.IsEpochValidAsync(tableRestricted, cachedEpoch: 1);

        // RED PHASE: Currently EpochValidationService uses substring matching on the name
        // (key.Contains("sensitive") || key.Contains("salary") ...), so it fails to identify
        // customer_data_export_42 as sensitive, returning true instead of false (fail-closed).
        isValid.ShouldBeFalse();
    }

    [Fact]
    public async Task ConnectionRestored_EvictsLocalL1EpochCache()
    {
        // POL-9: L1 local epoch cache must be evicted on Redis connection restored event
        // to prevent reusing stale epochs missed during partition/disconnect.
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);

        var service = new EpochValidationService(multiplexer: multiplexer);

        var table = new TableIdentifier("finance", "dbo", "invoices");
        await service.InvalidateEpochAsync(table);

        // Cached locally:
        var localEpochField = typeof(EpochValidationService).GetField("_epochs", BindingFlags.NonPublic | BindingFlags.Instance);
        var epochsDict = (System.Collections.IDictionary)localEpochField!.GetValue(service)!;
        epochsDict.Count.ShouldBeGreaterThan(0);

        // Raise ConnectionRestored
        multiplexer.ConnectionRestored += Raise.Event<EventHandler<ConnectionFailedEventArgs>>(
            multiplexer,
            new ConnectionFailedEventArgs(multiplexer, new IPEndPoint(IPAddress.Loopback, 6379), ConnectionType.Interactive, ConnectionFailureType.None, null!, "restored"));

        epochsDict.Count.ShouldBe(0);
    }
}
