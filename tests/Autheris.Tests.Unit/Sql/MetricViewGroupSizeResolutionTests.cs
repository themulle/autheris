namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Metric views resolve their minimum group size with the same rules as the governed aggregation path.</summary>
[Trait("Category", "Security")]
public sealed class MetricViewGroupSizeResolutionTests
{
    private static readonly TenantId Tenant = new("tenant_a");

    private static TableMetadata MetricView(int? catalogMinGroupSize = null, bool nonPersonal = false) =>
        new()
        {
            Identifier = new TableIdentifier("dbx", "analytics", "sales_mv"),
            MinGroupSize = catalogMinGroupSize,
            NonPersonal = nonPersonal ? true : null,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceType = "Databricks",
                SourceName = "dbx",
                SchemaName = "analytics",
                TableName = "sales_mv",
                ObjectKind = DiscoveryObjectKind.MetricView,
                IsActive = true
            },
            Columns = [new TableColumn { ColumnName = "region", DataType = "string", Role = MetricViewColumnRole.Dimension }]
        };

    private static GovernedSqlRewriter Rewriter(AnalyticsOptions? analytics, IProtectionSnapshotProvider? provider = null) =>
        new(
            Options.Create(new GatewayOptions()),
            analyticsOptions: analytics is null ? null : Options.Create(analytics),
            protectionProvider: provider);

    private static AnalyticsOptions LoweringEnabled()
    {
        var options = new AnalyticsOptions();
        options.Privacy.DefaultMinGroupSize = 5;
        options.Privacy.AllowCatalogLowering = true;
        options.Privacy.CatalogMinGroupSizeFloor = 1;
        return options;
    }

    private static EffectiveProtection Protection(ProtectionClass tableClass) =>
        new(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            null,
            tableClass,
            tableClass,
            null,
            new Dictionary<string, ColumnProtection>(),
            CatalogVisibilityMode.OnRequest);

    [Fact]
    public async Task Resolve_WithoutAnalyticsOptions_DefaultsToFive() =>
        (await Rewriter(null).ResolveMetricViewMinGroupSizeAsync(MetricView(), Tenant, CancellationToken.None)).ShouldBe(5);

    [Fact]
    public async Task Resolve_UsesOperatorDefault_WhenCatalogValueMissing()
    {
        var options = new AnalyticsOptions();
        options.Privacy.DefaultMinGroupSize = 8;

        (await Rewriter(options).ResolveMetricViewMinGroupSizeAsync(MetricView(), Tenant, CancellationToken.None)).ShouldBe(8);
    }

    [Fact]
    public async Task Resolve_CatalogValueAboveOperatorDefault_Wins()
    {
        var options = new AnalyticsOptions();
        options.Privacy.DefaultMinGroupSize = 5;

        (await Rewriter(options).ResolveMetricViewMinGroupSizeAsync(MetricView(catalogMinGroupSize: 12), Tenant, CancellationToken.None))
            .ShouldBe(12);
    }

    [Fact]
    public async Task Resolve_TableMinimumAndDatasetOverride_ApplyWithAggregationPrecedence()
    {
        var options = new AnalyticsOptions();
        options.Privacy.DefaultMinGroupSize = 5;
        options.TableMinimumGroupSizes["analytics.sales_mv"] = 7;
        (await Rewriter(options).ResolveMetricViewMinGroupSizeAsync(MetricView(), Tenant, CancellationToken.None)).ShouldBe(7);

        options.Privacy.Datasets["analytics.sales_mv"] = new DatasetPrivacyOverrideOptions { MinGroupSize = 9 };
        (await Rewriter(options).ResolveMetricViewMinGroupSizeAsync(MetricView(), Tenant, CancellationToken.None)).ShouldBe(9);
    }

    [Fact]
    public async Task Resolve_NoProtectionProvider_RefusesCatalogLowering()
    {
        var view = MetricView(catalogMinGroupSize: 1, nonPersonal: true);

        (await Rewriter(LoweringEnabled()).ResolveMetricViewMinGroupSizeAsync(view, Tenant, CancellationToken.None)).ShouldBe(5);
        (await Rewriter(LoweringEnabled(), new FakeProtectionProvider(_ => Protection(ProtectionClass.Internal)))
            .ResolveMetricViewMinGroupSizeAsync(view, Tenant, CancellationToken.None)).ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_ProtectionProviderThrows_RefusesCatalogLowering()
    {
        var view = MetricView(catalogMinGroupSize: 1, nonPersonal: true);
        var provider = new FakeProtectionProvider(_ => throw new InvalidOperationException("snapshot unavailable"));

        (await Rewriter(LoweringEnabled(), provider).ResolveMetricViewMinGroupSizeAsync(view, Tenant, CancellationToken.None)).ShouldBe(5);
    }

    [Fact]
    public async Task Resolve_ProviderCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var provider = new FakeProtectionProvider(_ => throw new OperationCanceledException(cts.Token));
        var rewriter = Rewriter(LoweringEnabled(), provider);

        await Should.ThrowAsync<OperationCanceledException>(
            () => rewriter.ResolveMetricViewMinGroupSizeAsync(MetricView(catalogMinGroupSize: 1, nonPersonal: true), Tenant, cts.Token));
    }

    private sealed class FakeProtectionProvider(Func<TableIdentifier, EffectiveProtection> get) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(get(table));

    }
}
