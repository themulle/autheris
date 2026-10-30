namespace Autheris.Tests.Unit.Catalog;

using System.Diagnostics.Metrics;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

/// <summary>Issue #111 WP-4 (A-9): tier gauge, startup and configuration audit, aggregated audit of tiers given to new tables.</summary>
[Collection("CatalogTelemetry")]
public sealed class CatalogVisibilityTelemetryTests
{
    private readonly ITableMetadataRepository _tables = Substitute.For<ITableMetadataRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    private static Dictionary<string, long> ReadGauge()
    {
        var values = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "autheris_catalog_visibility_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "tier")
                {
                    values[(string)tag.Value!] = value;
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return values;
    }

    [Fact]
    public async Task Refresh_PublishesTheCountsPerTier_WithoutTableNamesOrIds()
    {
        _tables.GetDatasetVisibilityCountsAsync(Arg.Any<CancellationToken>()).Returns(new DatasetVisibilityCounts(7, 3, 1));
        var metrics = new CatalogVisibilityMetrics(_tables, NullLogger<CatalogVisibilityMetrics>.Instance);

        await metrics.RefreshAsync(default);

        var gauge = ReadGauge();
        gauge["hidden"].ShouldBe(7);
        gauge["summary"].ShouldBe(3);
        gauge["full"].ShouldBe(1);
    }

    [Fact]
    public async Task Refresh_StoreFailure_KeepsTheLastValues_AndNeverThrows()
    {
        _tables.GetDatasetVisibilityCountsAsync(Arg.Any<CancellationToken>()).Returns(new DatasetVisibilityCounts(5, 2, 0));
        var metrics = new CatalogVisibilityMetrics(_tables, NullLogger<CatalogVisibilityMetrics>.Instance);
        await metrics.RefreshAsync(default);
        _tables.GetDatasetVisibilityCountsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));

        await metrics.RefreshAsync(default);

        ReadGauge()["summary"].ShouldBe(2);
    }

    private CatalogGovernanceHostedService Create(DatasetVisibility defaultVisibility = DatasetVisibility.Hidden, string? mode = null, StaticOptionsMonitor<GatewayOptions>? monitor = null)
        => new(_audit, monitor ?? new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = string.Equals(mode, "Single", StringComparison.Ordinal) ? "default-tenant" : null },
            Catalog = new DataCatalogOptions { DefaultVisibility = defaultVisibility }
        }), new CatalogVisibilityMetrics(_tables, NullLogger<CatalogVisibilityMetrics>.Instance), NullLogger<CatalogGovernanceHostedService>.Instance);

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_Startup_AuditsTheEffectiveDefaultVisibilityAndDefaultTenant()
    {
        _tables.GetDatasetVisibilityCountsAsync(Arg.Any<CancellationToken>()).Returns(new DatasetVisibilityCounts(0, 0, 0));

        await Create(DatasetVisibility.Summary, "Single").StartAsync(default);

        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.CatalogVisibilityChanged
            && e.DetailsJson.Contains("\"source\":\"startup\"")
            && e.DetailsJson.Contains("\"defaultVisibility\":\"Summary\"")
            && e.DetailsJson.Contains("\"defaultTenant\":\"Configured\"")), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_Startup_NoDefaultTenant_IsAuditedAsNone()
    {
        _tables.GetDatasetVisibilityCountsAsync(Arg.Any<CancellationToken>()).Returns(new DatasetVisibilityCounts(0, 0, 0));

        await Create().StartAsync(default);

        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e => e.DetailsJson.Contains("\"defaultTenant\":\"None\"")), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_FlushAggregated_WritesOneEntryWithTheCount_AndNothingWhenNoTierWasGiven()
    {
        CatalogDefaultTierTally.TakeAndReset();
        var service = Create(DatasetVisibility.Summary);

        await service.FlushDefaultTierTallyAsync(default);
        await _audit.DidNotReceive().RecordBestEffortAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());

        CatalogDefaultTierTally.Note();
        CatalogDefaultTierTally.Note();
        await service.FlushDefaultTierTallyAsync(default);

        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e =>
            e.DetailsJson.Contains("\"source\":\"ingestion\"") && e.DetailsJson.Contains("\"count\":2") && e.DetailsJson.Contains("\"defaultVisibility\":\"Summary\"")
            && e.TargetTable == string.Empty), Arg.Any<CancellationToken>());
        await service.FlushDefaultTierTallyAsync(default);
        await _audit.Received(1).RecordBestEffortAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_ConfigurationChange_IsAudited_OnlyWhenTheAuditedValuesChanged()
    {
        _tables.GetDatasetVisibilityCountsAsync(Arg.Any<CancellationToken>()).Returns(new DatasetVisibilityCounts(0, 0, 0));
        var service = Create();
        await service.StartAsync(default);
        _audit.ClearReceivedCalls();

        await service.OnOptionsChangedAsync(new GatewayOptions { Catalog = new DataCatalogOptions { DefaultVisibility = DatasetVisibility.Hidden } });
        await _audit.DidNotReceiveWithAnyArgs().RecordBestEffortAsync(default!, default);

        await service.OnOptionsChangedAsync(new GatewayOptions { Catalog = new DataCatalogOptions { DefaultVisibility = DatasetVisibility.Summary } });
        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e =>
            e.DetailsJson.Contains("\"source\":\"configuration\"") && e.DetailsJson.Contains("\"defaultVisibility\":\"Summary\"")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Tally_TakeAndReset_ReturnsTheCountOnce()
    {
        CatalogDefaultTierTally.TakeAndReset();
        CatalogDefaultTierTally.Note();

        CatalogDefaultTierTally.TakeAndReset().ShouldBe(1);
        CatalogDefaultTierTally.TakeAndReset().ShouldBe(0);
    }
}

[CollectionDefinition("CatalogTelemetry", DisableParallelization = true)]
public sealed class CatalogTelemetryCollection;
