namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// WP-0 (PLAN-GOV-111 A-0.1, A-0.3): REST list/detail/search and data sources go through the consent-based catalog visibility.
/// T-W0-1 (detail part), T-W0-6, T-W0-7, T-W0-8, T-W0-14.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogDiscoveryConsentVisibilityTests
{
    private static readonly TableMetadata OrdersA = Wp0.Table("sales", "orders_a", "Orders of tenant A", ["id", "amount", "secret_note"], baseUrl: "https://a.internal/api");
    private static readonly TableMetadata InvoicesB = Wp0.Table("sales", "invoices_b", "Invoices of tenant B", ["id", "total"], baseUrl: "https://b.internal/api");
    private static readonly TableMetadata Countries = Wp0.Table("ref", "countries", "Shared reference data", ["code"]);

    private static (CatalogDiscoveryServiceTestHandle Svc, IConsentRepository Consents) Create(
        GatewayOptions? options = null, IRebacEvaluator? rebac = null, ILogger<Autheris.Application.Catalog.Services.CatalogDiscoveryService>? logger = null, Consent[]? consents = null)
    {
        var repo = Wp0.Consents(consents ?? []);
        return (new CatalogDiscoveryServiceTestHandle(Wp0.Service(Wp0.Tables(OrdersA, InvoicesB, Countries), repo, options, rebac: rebac, logger: logger)), repo);
    }

    private static string[] Names(IEnumerable<CatalogDatasetSummary> list) => list.Select(d => d.Table).OrderBy(n => n).ToArray();

    [Fact]
    public async Task List_CallerWithoutConsent_ReBacOff_SeesNothing()
    {
        var (svc, _) = Create();
        var user = Wp0.User("S-B-USER", "tenant-b");

        (await svc.Inner.ListDatasetsAsync(Wp0.Context(user))).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_ConsentedCaller_SeesOnlyConsentedTables()
    {
        var (svc, _) = Create(consents: [Wp0.Allow(OrdersA.Identifier, "S-A-USER", Wp0.TenantA)]);
        var user = Wp0.User("S-A-USER", "tenant-a");

        Names(await svc.Inner.ListDatasetsAsync(Wp0.Context(user))).ShouldBe(["orders_a"]);
    }

    [Fact]
    public async Task List_AnonymousOrUnauthenticatedContext_SeesNothing_NeverTheOldUnfilteredCatalog()
    {
        var (svc, _) = Create(consents: [Wp0.Allow(OrdersA.Identifier, "S-A-USER", Wp0.TenantA)]);

        (await svc.Inner.ListDatasetsAsync(new RequestContext())).ShouldBeEmpty();
        (await svc.Inner.ListDatasetsAsync(new RequestContext(Wp0.TenantA, new Sid("S-A-USER"), ["GovernanceAdmin", "ClusterAdmin"]))).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_ClusterAdminAndSingleModeGovernanceAdmin_SeeEverything()
    {
        var (single, _) = Create(Wp0.Options(TenancyMode.Single));
        var (multi, _) = Create(Wp0.Options(TenancyMode.Multi));

        Names(await multi.Inner.ListDatasetsAsync(Wp0.Context(Wp0.User("root", null, ["ClusterAdmin"])))).Length.ShouldBe(3);
        Names(await single.Inner.ListDatasetsAsync(Wp0.Context(Wp0.User("gov", null, ["GovernanceAdmin"])))).Length.ShouldBe(3);
    }

    // T-W0-6 (REST part): a tenant-B GovernanceAdmin in Multi mode sees no tenant-A table and no BaseUrl of a tenant-A table of the same domain.
    [Fact]
    public async Task TenantScopedGovernanceAdmin_Multi_ListDetailSearchDatasources_ShowNoForeignTenantTable()
    {
        var (svc, _) = Create(Wp0.Options(TenancyMode.Multi, "ref.dbo.countries"),
            consents: [Wp0.Allow(InvoicesB.Identifier, "S-B-OWNER", Wp0.TenantB), Wp0.Allow(OrdersA.Identifier, "S-A-OWNER", Wp0.TenantA)]);
        var admin = Wp0.Context(Wp0.User("S-B-ADMIN", "tenant-b", ["GovernanceAdmin"]));

        Names(await svc.Inner.ListDatasetsAsync(admin)).ShouldBe(["countries", "invoices_b"]);
        (await svc.Inner.GetDatasetDetailAsync(OrdersA.Identifier, admin)).ShouldBeNull();
        (await svc.Inner.GetDatasetDetailAsync(InvoicesB.Identifier, admin)).ShouldNotBeNull();
        Names(await svc.Inner.SearchCatalogAsync("Orders", null, admin)).ShouldBeEmpty();

        var sources = await svc.Inner.ListDatasourcesAsync(admin);
        sources.Select(s => s.BaseUrl).ShouldNotContain("https://a.internal/api");
        sources.Select(s => s.BaseUrl).ShouldContain("https://b.internal/api");
    }

    [Fact]
    public async Task Datasources_NonAdminAndClaimlessAdmin_AreEmpty_ClusterAdminAndSingleAdminSeeAll()
    {
        var (multi, _) = Create(Wp0.Options(TenancyMode.Multi));
        var (single, _) = Create(Wp0.Options(TenancyMode.Single));

        (await multi.Inner.ListDatasourcesAsync(Wp0.Context(Wp0.User("S-USER", "tenant-a")))).ShouldBeEmpty();
        (await multi.Inner.ListDatasourcesAsync(Wp0.Context(Wp0.User("S-ADM", null, ["GovernanceAdmin"])))).ShouldBeEmpty();
        (await multi.Inner.ListDatasourcesAsync(new RequestContext())).ShouldBeEmpty();
        (await multi.Inner.ListDatasourcesAsync(Wp0.Context(Wp0.User("root", null, ["ClusterAdmin"])))).Select(s => s.BaseUrl)
            .ShouldContain("https://a.internal/api");
        (await single.Inner.ListDatasourcesAsync(Wp0.Context(Wp0.User("S-ADM", null, ["GovernanceAdmin"])))).Count.ShouldBeGreaterThan(0);
    }

    // T-W0-14
    [Fact]
    public async Task ClaimlessGovernanceAdmin_Multi_SeesConsentedTablesOnly_AndDowngradeIsLoggedOncePerWindow()
    {
        var logger = Substitute.For<ILogger<Autheris.Application.Catalog.Services.CatalogDiscoveryService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var sid = "S-CLAIMLESS-" + Guid.NewGuid().ToString("N");
        var (svc, _) = Create(Wp0.Options(TenancyMode.Multi), logger: logger, consents: [Wp0.Allow(Countries.Identifier, sid, TenantId.LegacySingleTenant)]);
        var admin = Wp0.Context(Wp0.User(sid, null, ["GovernanceAdmin"]));

        Names(await svc.Inner.ListDatasetsAsync(admin)).ShouldBe(["countries"]);
        await svc.Inner.ListDatasetsAsync(admin);

        logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning).ShouldBe(1);
    }

    // T-W0-1 (detail part): a column-level consent shows only the consented columns.
    [Fact]
    public async Task Detail_ColumnLevelConsent_ReportsOnlyConsentedColumns()
    {
        var (svc, _) = Create(consents: [Wp0.Allow(OrdersA.Identifier, "S-A-USER", Wp0.TenantA, "id", "amount")]);
        var user = Wp0.Context(Wp0.User("S-A-USER", "tenant-a"));

        var detail = await svc.Inner.GetDatasetDetailAsync(OrdersA.Identifier, user);

        detail.ShouldNotBeNull();
        detail.Columns.Select(c => c.Name).ShouldBe(["id", "amount"]);
        System.Text.Json.JsonSerializer.Serialize(detail).ShouldNotContain("secret_note");
    }

    // T-W0-8
    [Fact]
    public async Task Detail_UnknownUnconsentedAndInactiveUnconsentedDataset_AreAllNull()
    {
        var inactive = Wp0.Table("sales", "retired", "Retired", active: false);
        var repo = Wp0.Consents(Wp0.Allow(OrdersA.Identifier, "S-A-USER", Wp0.TenantA));
        var svc = Wp0.Service(Wp0.Tables(OrdersA, inactive), repo);
        var user = Wp0.Context(Wp0.User("S-A-USER", "tenant-a"));

        (await svc.GetDatasetDetailAsync(new TableIdentifier("sales", "dbo", "does_not_exist"), user)).ShouldBeNull();
        (await svc.GetDatasetDetailAsync(inactive.Identifier, user)).ShouldBeNull();
        (await svc.GetDatasetDetailAsync(InvoicesB.Identifier, user)).ShouldBeNull();
        (await svc.GetDatasetDetailAsync(OrdersA.Identifier, user)).ShouldNotBeNull();
    }

    // T-W0-7
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public async Task ReBacOn_IsAnAdditionalFilter_NeverAUnion(bool rebacAllows, bool hasConsent, bool expectedVisible)
    {
        var rebac = Substitute.For<IRebacEvaluator>();
        rebac.IsEnabled.Returns(true);
        rebac.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns(rebacAllows ? RebacCheckResult.Permitted : RebacCheckResult.Denied);
        var consents = hasConsent ? new[] { Wp0.Allow(OrdersA.Identifier, "S-A-USER", Wp0.TenantA) } : Array.Empty<Consent>();
        var (svc, _) = Create(rebac: rebac, consents: consents);
        var user = Wp0.Context(Wp0.User("S-A-USER", "tenant-a"));

        (await svc.Inner.ListDatasetsAsync(user)).Any(d => d.Table == "orders_a").ShouldBe(expectedVisible);
        (await svc.Inner.GetDatasetDetailAsync(OrdersA.Identifier, user) != null).ShouldBe(expectedVisible);
    }

    [Fact]
    public async Task ThrowingConsentRepository_YieldsEmptyAndNull_NeverTheUnfilteredCatalog()
    {
        var consents = Substitute.For<IConsentRepository>();
        consents.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Consent>>(_ => throw new InvalidOperationException("store down"));
        var svc = Wp0.Service(Wp0.Tables(OrdersA), consents);
        var user = Wp0.Context(Wp0.User("S-A-USER", "tenant-a"));

        (await svc.ListDatasetsAsync(user)).ShouldBeEmpty();
        (await svc.GetDatasetDetailAsync(OrdersA.Identifier, user)).ShouldBeNull();
        (await svc.SearchCatalogAsync("orders", null, user)).ShouldBeEmpty();
    }

    // Thin holder so the tuple stays readable.
    internal sealed record CatalogDiscoveryServiceTestHandle(Autheris.Application.Catalog.Services.CatalogDiscoveryService Inner);
}
