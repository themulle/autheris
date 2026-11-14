namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class GatewayHealthCheckServiceTests
{
    [Fact]
    public async Task CatalogSearchIndex_FailedRebuild_ReportsDegraded()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var engine = new FakeHealthCatalogSearchEngine(
            isReady: false,
            indexedTableCount: 0,
            lastIndexedAt: null,
            status: new CatalogSearchIndexStatus(null, time.GetUtcNow(), CatalogSearchErrorCodes.RebuildFailed, null),
            indexedVersion: null);

        var service = new GatewayHealthCheckService(
            Options.Create(new GatewayOptions()),
            NullLogger<GatewayHealthCheckService>.Instance,
            catalogSearchEngine: engine,
            timeProvider: time);

        var report = await service.CheckHealthAsync();

        var comp = report.Components.Single(c => c.Name == "CatalogSearchIndex");
        comp.IsHealthy.ShouldBeTrue();
        report.IsHealthy.ShouldBeTrue();
        comp.Description.ShouldNotBeNull();
        comp.Description!.ShouldStartWith("degraded: ");
        comp.Description.ShouldContain(CatalogSearchErrorCodes.RebuildFailed);
    }

    [Fact]
    public async Task CatalogSearchIndex_StaleIndex_ReportsDegraded()
    {
        var t0 = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0.AddSeconds(46)); // > 3 * 15s
        var engine = new FakeHealthCatalogSearchEngine(
            isReady: true,
            indexedTableCount: 10,
            lastIndexedAt: t0,
            status: new CatalogSearchIndexStatus(t0, t0, null, TimeSpan.FromSeconds(46)),
            indexedVersion: (1, 1));
        var versionProvider = new FixedVersionProvider(new CatalogVersion(2, 1));

        var service = new GatewayHealthCheckService(
            Options.Create(new GatewayOptions()),
            NullLogger<GatewayHealthCheckService>.Instance,
            catalogSearchEngine: engine,
            catalogVersionProvider: versionProvider,
            catalogSearchOptions: Options.Create(new CatalogSearchOptions { RefreshSeconds = 15 }),
            timeProvider: time);

        var report = await service.CheckHealthAsync();

        var comp = report.Components.Single(c => c.Name == "CatalogSearchIndex");
        comp.IsHealthy.ShouldBeTrue();
        report.IsHealthy.ShouldBeTrue();
        comp.Description.ShouldNotBeNull();
        comp.Description!.ShouldStartWith("degraded: ");
    }

    [Fact]
    public async Task CatalogSearchIndex_AgedIndexWithSameVersion_RemainsHealthy()
    {
        var t0 = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0.AddMinutes(30));
        var engine = new FakeHealthCatalogSearchEngine(
            isReady: true,
            indexedTableCount: 10,
            lastIndexedAt: t0,
            status: new CatalogSearchIndexStatus(t0, t0, null, TimeSpan.FromMinutes(30)),
            indexedVersion: (2, 1));
        var versionProvider = new FixedVersionProvider(new CatalogVersion(2, 1));

        var service = new GatewayHealthCheckService(
            Options.Create(new GatewayOptions()),
            NullLogger<GatewayHealthCheckService>.Instance,
            catalogSearchEngine: engine,
            catalogVersionProvider: versionProvider,
            catalogSearchOptions: Options.Create(new CatalogSearchOptions { RefreshSeconds = 15 }),
            timeProvider: time);

        var report = await service.CheckHealthAsync();

        var comp = report.Components.Single(c => c.Name == "CatalogSearchIndex");
        comp.IsHealthy.ShouldBeTrue();
        comp.Description.ShouldNotBeNull();
        comp.Description!.ShouldStartWith("Catalog search index ready:");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CatalogSearchIndex_DegradedDescription_ContainsNoExceptionText()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var engine = new FakeHealthCatalogSearchEngine(
            isReady: true,
            indexedTableCount: 5,
            lastIndexedAt: time.GetUtcNow().AddSeconds(-10),
            status: new CatalogSearchIndexStatus(
                time.GetUtcNow().AddSeconds(-10),
                time.GetUtcNow(),
                CatalogSearchErrorCodes.EmbeddingFailed,
                TimeSpan.FromSeconds(10)),
            indexedVersion: (1, 1));

        var service = new GatewayHealthCheckService(
            Options.Create(new GatewayOptions()),
            NullLogger<GatewayHealthCheckService>.Instance,
            catalogSearchEngine: engine,
            timeProvider: time);

        var report = await service.CheckHealthAsync();

        var comp = report.Components.Single(c => c.Name == "CatalogSearchIndex");
        comp.IsHealthy.ShouldBeTrue();
        comp.Description.ShouldNotBeNull();
        comp.Description!.ShouldStartWith("degraded: ");
        comp.Description.ShouldNotContain("Exception");
        comp.Description.ShouldNotContain("StackTrace");
        comp.Description.ShouldNotContain("secret");
        comp.Description.ShouldNotContain("password");
    }

    [Fact]
    public async Task CatalogVersionSync_DirtyFlag_ReportsDegradedWithoutExceptionText()
    {
        var dirty = new CatalogDirtyFlag();
        var service = new GatewayHealthCheckService(
            Options.Create(new GatewayOptions()),
            NullLogger<GatewayHealthCheckService>.Instance,
            catalogDirtyFlag: dirty);

        (await service.CheckHealthAsync()).Components.ShouldNotContain(c => c.Name == "CatalogVersionSync");

        dirty.MarkDirty();
        var report = await service.CheckHealthAsync();

        var comp = report.Components.Single(c => c.Name == "CatalogVersionSync");
        comp.IsHealthy.ShouldBeTrue();
        report.IsHealthy.ShouldBeTrue();
        comp.Description.ShouldNotBeNull();
        comp.Description!.ShouldStartWith("degraded: ");
        comp.Description.ShouldNotContain("Exception");
    }

    private sealed class FixedVersionProvider(CatalogVersion version) : ICatalogVersionProvider
    {
        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => new(version);
        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(version.Formatted);
    }

    private sealed class FakeHealthCatalogSearchEngine(
        bool isReady,
        int indexedTableCount,
        DateTimeOffset? lastIndexedAt,
        CatalogSearchIndexStatus status,
        (long StructureVersion, long ContentVersion)? indexedVersion) : ICatalogSearchEngine, ICatalogSearchStatusReader
    {
        public bool IsIndexReady => isReady;
        public int IndexedTableCount => indexedTableCount;
        public DateTimeOffset? LastIndexedAt => lastIndexedAt;
        public (long StructureVersion, long ContentVersion)? LastIndexedVersion => indexedVersion;

        public CatalogSearchIndexStatus GetStatus(DateTimeOffset? utcNow = null) => status;

        public Task RebuildIndexAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<CatalogSearchHit> Search(CatalogSearchQuery query, Func<TableIdentifier, bool> isVisiblePredicate, Func<TableIdentifier, bool>? isColumnRestricted = null) => [];
        public Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(CatalogSearchQuery query, Func<TableIdentifier, bool> isVisiblePredicate, Func<TableIdentifier, bool>? isColumnRestricted = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogSearchHit>>([]);
    }
}
