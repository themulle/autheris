namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Flight SQL table listing: catalog domains are data source names, not tenants. A table is listed when its data
/// source is queryable for the tenant through WebSQL (the path Flight SQL executes on) and the caller may discover it.
/// </summary>
public sealed class FlightSqlTableListingTests
{
    private static readonly TenantId Tenant = new("tenant_finance");

    private static readonly ClaimsPrincipal Admin =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "Bearer"));

    private static TableMetadata Table(string domain, string schema, string table) => new()
    {
        Identifier = new TableIdentifier(domain, schema, table),
        Table = new Table { SourceName = domain, SchemaName = schema, TableName = table }
    };

    private static ArrowFlightSqlServer CreateServer(WebSqlOptions webSql, params TableMetadata[] tables)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(tables));
        var options = Options.Create(new GatewayOptions { WebSql = webSql });
        return new ArrowFlightSqlServer(Substitute.For<IArrowExportService>(), repo, options, NullLogger<ArrowFlightSqlServer>.Instance);
    }

    [Fact]
    public async Task TablesOfAQueryableDataSource_AreListed()
    {
        var server = CreateServer(
            new WebSqlOptions { Enabled = true, AllowedDataSources = ["lwetem_prod"] },
            Table("lwetem_prod", "md", "crane"), Table("lwetem_prod", "fms", "air1"));

        var tables = await server.GetTablesAsync(Admin, Tenant);

        tables.Select(t => $"{t.Catalog}.{t.Schema}.{t.TableName}").ShouldBe(["lwetem_prod.md.crane", "lwetem_prod.fms.air1"], ignoreOrder: true);
    }

    [Fact]
    public async Task TablesOfTheDefaultDataSource_AreListed()
    {
        var server = CreateServer(new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" }, Table("default", "public", "orders"));

        (await server.GetTablesAsync(Admin, Tenant)).ShouldHaveSingleItem().TableName.ShouldBe("orders");
    }

    [Fact]
    public async Task DataSourceNotEnabledForWebSql_IsNotListed()
    {
        var server = CreateServer(new WebSqlOptions { Enabled = true }, Table("hr", "public", "salaries"));

        (await server.GetTablesAsync(Admin, Tenant)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DataSourceOutsideTheTenantAllowlist_IsNotListed()
    {
        var server = CreateServer(
            new WebSqlOptions
            {
                Enabled = true,
                AllowedDataSources = ["lwetem_prod", "hr"],
                TenantDataSourceAllowlist = new(StringComparer.OrdinalIgnoreCase) { ["tenant_finance"] = ["lwetem_prod"] }
            },
            Table("lwetem_prod", "md", "crane"), Table("hr", "public", "salaries"));

        (await server.GetTablesAsync(Admin, Tenant)).ShouldHaveSingleItem().TableName.ShouldBe("crane");
    }

    [Fact]
    public async Task WebSqlDisabled_ListsNothing()
    {
        var server = CreateServer(new WebSqlOptions { Enabled = false, AllowedDataSources = ["lwetem_prod"] }, Table("lwetem_prod", "md", "crane"));

        (await server.GetTablesAsync(Admin, Tenant)).ShouldBeEmpty();
    }
}
