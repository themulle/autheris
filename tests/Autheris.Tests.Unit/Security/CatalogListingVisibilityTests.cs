namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Serialization;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Befund 3.4 / Wunsch 9: the Iceberg REST catalog and Arrow Flight SQL listed every table of the caller's tenant without
/// looking at consents. They now apply the same catalog visibility as the GraphQL <c>catalog</c> field and MCP:
/// a table is listed only with an active Allow consent; GovernanceAdmin and ClusterAdmin see everything.
/// </summary>
public sealed class CatalogListingVisibilityTests
{
    private const string Tenant = "tenant-1";
    private const string AnalystSid = "S-1-5-21-ANALYST";

    private static readonly TableIdentifier OrdersId = new(Tenant, "raw", "orders");
    private static readonly TableIdentifier SalariesId = new(Tenant, "hr", "salaries");

    private static TableMetadata Lakehouse(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DataSourceType = DataSourceType.LakehouseIceberg },
        Columns = [new TableColumn { ColumnName = "id" }]
    };

    private static ClaimsPrincipal User(params string[] roles) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.PrimarySid, AnalystSid), new Claim("tenant_id", Tenant) }
            .Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))),
        "Bearer"));

    private static Consent AllowOrders() => new()
    {
        TableIdentifier = OrdersId,
        TenantId = new TenantId(Tenant),
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(AnalystSid),
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1)
    };

    private static ITableMetadataRepository Tables()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Lakehouse(OrdersId), Lakehouse(SalariesId)]));
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(null));
        return repo;
    }

    private static IConsentRepository Consents(params Consent[] consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents));
        return repo;
    }

    // Flight SQL lists the tables of data sources the tenant may query through WebSQL (catalog domain = data source).
    private static ArrowFlightSqlServer Flight(IConsentRepository? consents) => new(
        Substitute.For<IArrowExportService>(),
        Tables(),
        Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, AllowedDataSources = [Tenant] } }),
        NullLogger<ArrowFlightSqlServer>.Instance,
        consents);

    private static IcebergRestCatalogFederationService Iceberg(IConsentRepository? consents) => new(
        Substitute.For<IIcebergMetadataReader>(),
        Tables(),
        Options.Create(new GatewayOptions()),
        NullLogger<IcebergRestCatalogFederationService>.Instance,
        new ConsentResolutionService(),
        consents);

    [Fact]
    public async Task FlightSql_GetTables_ListsOnlyConsentedTables()
    {
        var tables = await Flight(Consents(AllowOrders())).GetTablesAsync(User(), new TenantId(Tenant));

        tables.Select(t => t.TableName).ShouldBe(["orders"]);
    }

    [Fact]
    public async Task FlightSql_GetTables_WithoutConsent_ListsNothing()
    {
        var tables = await Flight(Consents()).GetTablesAsync(User(), new TenantId(Tenant));

        tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task FlightSql_GetTables_WithoutConsentRepository_FailsClosed()
    {
        var tables = await Flight(null).GetTablesAsync(User(), new TenantId(Tenant));

        tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task FlightSql_GetTables_GovernanceAdmin_ListsWholeTenantCatalog()
    {
        var tables = await Flight(Consents()).GetTablesAsync(User("GovernanceAdmin"), new TenantId(Tenant));

        tables.Select(t => t.TableName).OrderBy(n => n).ShouldBe(["orders", "salaries"]);
    }

    [Fact]
    public async Task Iceberg_ListNamespacesAndTables_ShowOnlyConsentedTables()
    {
        var service = Iceberg(Consents(AllowOrders()));

        (await service.ListNamespacesAsync(Tenant, User())).ShouldBe(["raw"]);
        (await service.ListTablesAsync(Tenant, "raw", User())).ShouldBe(["orders"]);
        (await service.ListTablesAsync(Tenant, "hr", User())).ShouldBeEmpty();
    }

    [Fact]
    public async Task Iceberg_ListNamespaces_WithoutConsent_ListsNothing()
    {
        var service = Iceberg(Consents());

        (await service.ListNamespacesAsync(Tenant, User())).ShouldBeEmpty();
    }

    [Fact]
    public async Task Iceberg_ListTables_WithoutConsentRepository_FailsClosed()
    {
        var service = Iceberg(null);

        (await service.ListTablesAsync(Tenant, "raw", User())).ShouldBeEmpty();
    }

    [Fact]
    public async Task Iceberg_LoadTable_UnknownTable_LooksLikeDeniedTable()
    {
        var service = Iceberg(Consents());

        var ex = await Should.ThrowAsync<SecurityException>(() =>
            service.LoadTableAsync(Tenant, "raw", "does_not_exist", User()).AsTask());

        ex.Message.ShouldBe("Access to table 'raw.does_not_exist' denied by policy.");
    }
}
