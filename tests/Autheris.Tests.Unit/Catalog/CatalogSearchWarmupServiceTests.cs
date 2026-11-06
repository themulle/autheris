namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Autheris.Tests.Common;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CatalogSearchWarmupServiceTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ICatalogSearchEngine _searchEngine = Substitute.For<ICatalogSearchEngine>();
    private readonly ICatalogVersionProvider _versionProvider = Substitute.For<ICatalogVersionProvider>();

    private static TableMetadata CreateTable(string table) => new()
    {
        Identifier = new TableIdentifier("default", "public", table),
        Table = new Table { Id = Guid.NewGuid(), SourceName = "default", SchemaName = "public", TableName = table, DisplayName = table, IsActive = true },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    [Fact]
    public async Task WarmupOnStartup_BuildsIndexOnce()
    {
        var tables = new List<TableMetadata> { CreateTable("t1") };
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(tables);
        _versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVersion>(new CatalogVersion(1, 1)));

        var options = Options.Create(new CatalogSearchOptions
        {
            Enabled = true,
            WarmupOnStartup = true,
            RefreshSeconds = 0
        });

        using var service = new CatalogSearchWarmupService(
            _metadataRepo,
            _searchEngine,
            options,
            NullLogger<CatalogSearchWarmupService>.Instance,
            versionProvider: _versionProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.StartAsync(cts.Token);
        await WaitHelper.WaitUntil(() => _searchEngine.ReceivedCalls().Any(), TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        await _searchEngine.Received(1).RebuildIndexAsync(tables, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CatalogChangedEvent_RebuildsIndex()
    {
        var tables1 = new List<TableMetadata> { CreateTable("t1") };
        var tables2 = new List<TableMetadata> { CreateTable("t1"), CreateTable("t2") };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(tables1, tables2);

        Func<object, Task>? eventHandler = null;
        var eventBus = Substitute.For<IEventBus>();
        eventBus.Subscribe<object>(ICatalogVersionProvider.CatalogChangedEventChannel, Arg.Do<Func<object, Task>>(h => eventHandler = h))
            .Returns(Substitute.For<IDisposable>());

        var options = Options.Create(new CatalogSearchOptions
        {
            Enabled = true,
            WarmupOnStartup = true,
            RefreshSeconds = 0
        });

        using var service = new CatalogSearchWarmupService(
            _metadataRepo,
            _searchEngine,
            options,
            NullLogger<CatalogSearchWarmupService>.Instance,
            eventBus: eventBus,
            versionProvider: _versionProvider);

        _versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(
                new ValueTask<CatalogVersion>(new CatalogVersion(1, 1)),
                new ValueTask<CatalogVersion>(new CatalogVersion(2, 2)));

        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => _searchEngine.ReceivedCalls().Any(), TimeSpan.FromSeconds(5));

        // Publish catalog:changed via captured handler
        eventHandler.ShouldNotBeNull();
        await eventHandler(new object());
        await WaitHelper.WaitUntil(() => _searchEngine.ReceivedCalls().Count() >= 2, TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);

        await _searchEngine.Received(2).RebuildIndexAsync(Arg.Any<IReadOnlyList<TableMetadata>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PeriodicRefresh_SkipsWhenVersionUnchanged()
    {
        var tables = new List<TableMetadata> { CreateTable("t1") };
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(tables);

        // Version stays at (1, 1)
        _versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVersion>(new CatalogVersion(1, 1)));

        var options = Options.Create(new CatalogSearchOptions
        {
            Enabled = true,
            WarmupOnStartup = true,
            RefreshSeconds = 1 // 1 second interval
        });

        using var service = new CatalogSearchWarmupService(
            _metadataRepo,
            _searchEngine,
            options,
            NullLogger<CatalogSearchWarmupService>.Instance,
            versionProvider: _versionProvider);

        await service.StartAsync(CancellationToken.None);
        try { await WaitHelper.WaitUntil(() => false, TimeSpan.FromMilliseconds(1200)); } catch (TimeoutException) { }
        await service.StopAsync(CancellationToken.None);

        // Only the initial warmup build should have occurred
        await _searchEngine.Received(1).RebuildIndexAsync(tables, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HealthCheck_ExposesCatalogSearchIndexComponent()
    {
        var engine = Substitute.For<ICatalogSearchEngine>();
        engine.IsIndexReady.Returns(true);
        engine.IndexedTableCount.Returns(42);
        var time = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        engine.LastIndexedAt.Returns(time);

        var healthService = new GatewayHealthCheckService(
            Options.Create(new GatewayOptions()),
            NullLogger<GatewayHealthCheckService>.Instance,
            catalogSearchEngine: engine);

        var report = await healthService.CheckHealthAsync();

        var comp = report.Components.FirstOrDefault(c => c.Name == "CatalogSearchIndex");
        comp.Name.ShouldBe("CatalogSearchIndex");
        comp.IsHealthy.ShouldBeTrue();
        comp.Description.ShouldNotBeNull();
        comp.Description.ShouldContain("42 tables");
        comp.Description.ShouldContain("2026-11-04");
    }
}
