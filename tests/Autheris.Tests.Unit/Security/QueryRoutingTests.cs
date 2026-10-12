namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Analysis;
using Xunit;

public sealed class QueryRoutingTests
{
    private const string Tenant = "tenant_test";

    private static TableMetadata CreateTable(string domain, string table, DataSourceType dsType, string? sourceName = null)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, "public", table),
            Table = new Table
            {
                TableName = table,
                SchemaName = "public",
                SourceType = dsType == DataSourceType.Sql ? "PostgreSql" : "Http",
                DataSourceType = dsType,
                SourceName = sourceName ?? (dsType == DataSourceType.Sql ? domain : string.Empty),
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>()
        };
    }

    private static SqlQueryMetadata CreateMetadata(IReadOnlyList<TableAccessTarget> tables, SqlStatementType type = SqlStatementType.Select) =>
        new(
            StatementType: type,
            ReferencedTables: tables,
            ProjectedColumns: ["id"],
            JoinCount: tables.Count > 1 ? tables.Count - 1 : 0,
            MaxSubqueryDepth: 0,
            HasExplicitLimit: false,
            ExplicitLimitValue: null);

    [Fact]
    public async Task SingleSqlSource_ClassifiesAsHomogeneousSql()
    {
        var meta = CreateTable("crm", "customers", DataSourceType.Sql, "crm_db");
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(meta);

        var router = new CrossSourceQueryRouter(
            repo,
            new CrossSourceOptions { Enabled = true },
            new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["crm"] });

        var target = new TableAccessTarget("crm", "public", "customers", null, "crm.public.customers");
        var sqlMeta = CreateMetadata([target]);

        var decision = await router.RouteAsync(sqlMeta, null, new TenantId(Tenant), isDml: false);

        decision.SourceClass.ShouldBe(QuerySourceClass.HomogeneousSql);
    }

    [Fact]
    public async Task TwoTables_SameSqlDataSource_ClassifiesAsHomogeneousSql()
    {
        var meta1 = CreateTable("crm", "customers", DataSourceType.Sql, "crm_db");
        var meta2 = CreateTable("crm", "orders", DataSourceType.Sql, "crm_db");

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(new TableIdentifier("crm", "public", "customers"), Arg.Any<CancellationToken>()).Returns(meta1);
        repo.GetTableMetadataAsync(new TableIdentifier("crm", "public", "orders"), Arg.Any<CancellationToken>()).Returns(meta2);

        var router = new CrossSourceQueryRouter(
            repo,
            new CrossSourceOptions { Enabled = true },
            new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["crm"] });

        var target1 = new TableAccessTarget("crm", "public", "customers", "c", "crm.public.customers");
        var target2 = new TableAccessTarget("crm", "public", "orders", "o", "crm.public.orders");
        var sqlMeta = CreateMetadata([target1, target2]);

        var decision = await router.RouteAsync(sqlMeta, null, new TenantId(Tenant), isDml: false);

        decision.SourceClass.ShouldBe(QuerySourceClass.HomogeneousSql);
    }

    [Fact]
    public async Task TwoTables_DifferentSqlDataSources_WhenEnabled_ClassifiesAsCrossSource()
    {
        var meta1 = CreateTable("crm", "customers", DataSourceType.Sql, "crm_db");
        var meta2 = CreateTable("erp", "invoices", DataSourceType.Sql, "erp_db");

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(new TableIdentifier("crm", "public", "customers"), Arg.Any<CancellationToken>()).Returns(meta1);
        repo.GetTableMetadataAsync(new TableIdentifier("erp", "public", "invoices"), Arg.Any<CancellationToken>()).Returns(meta2);

        var router = new CrossSourceQueryRouter(
            repo,
            new CrossSourceOptions { Enabled = true },
            new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["crm", "erp"] });

        var target1 = new TableAccessTarget("crm", "public", "customers", "c", "crm.public.customers");
        var target2 = new TableAccessTarget("erp", "public", "invoices", "i", "erp.public.invoices");
        var sqlMeta = CreateMetadata([target1, target2]);

        var decision = await router.RouteAsync(sqlMeta, null, new TenantId(Tenant), isDml: false);

        decision.SourceClass.ShouldBe(QuerySourceClass.CrossSource);
        decision.Tables.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TwoTables_SqlAndHttpDeclarative_WhenEnabled_ClassifiesAsCrossSource()
    {
        var sqlMeta = CreateTable("crm", "customers", DataSourceType.Sql, "crm_db");
        var httpMeta = CreateTable("logistics", "shipments", DataSourceType.HttpDeclarative);

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(new TableIdentifier("crm", "public", "customers"), Arg.Any<CancellationToken>()).Returns(sqlMeta);
        repo.GetTableMetadataAsync(new TableIdentifier("logistics", "public", "shipments"), Arg.Any<CancellationToken>()).Returns(httpMeta);

        var router = new CrossSourceQueryRouter(
            repo,
            new CrossSourceOptions { Enabled = true },
            new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["crm", "logistics"] });

        var target1 = new TableAccessTarget("crm", "public", "customers", "c", "crm.public.customers");
        var target2 = new TableAccessTarget("logistics", "public", "shipments", "s", "logistics.public.shipments");
        var meta = CreateMetadata([target1, target2]);

        var decision = await router.RouteAsync(meta, null, new TenantId(Tenant), isDml: false);

        decision.SourceClass.ShouldBe(QuerySourceClass.CrossSource);
    }

    [Theory]
    [InlineData(DataSourceType.HttpPlugin)]
    [InlineData(DataSourceType.LakehouseIceberg)]
    [InlineData(DataSourceType.LakehouseDelta)]
    public async Task UnsupportedDataSourceType_ThrowsWebSqlPolicyException(DataSourceType unsupportedType)
    {
        var meta = CreateTable("lake", "events", unsupportedType);
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(meta);

        var router = new CrossSourceQueryRouter(
            repo,
            new CrossSourceOptions { Enabled = true },
            new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["lake"] });

        var target = new TableAccessTarget("lake", "public", "events", null, "lake.public.events");
        var sqlMeta = CreateMetadata([target]);

        await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await router.RouteAsync(sqlMeta, null, new TenantId(Tenant), isDml: false));
    }

    [Fact]
    public async Task CrossSource_WhenDisabled_ThrowsClassicCrossCatalogMessage()
    {
        var meta1 = CreateTable("cat1", "orders", DataSourceType.Sql, "db1");
        var meta2 = CreateTable("cat2", "orders", DataSourceType.Sql, "db2");

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(new TableIdentifier("cat1", "public", "orders"), Arg.Any<CancellationToken>()).Returns(meta1);
        repo.GetTableMetadataAsync(new TableIdentifier("cat2", "public", "orders"), Arg.Any<CancellationToken>()).Returns(meta2);

        var router = new CrossSourceQueryRouter(
            repo,
            new CrossSourceOptions { Enabled = false }, // Disabled!
            new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["cat1", "cat2"] });

        var target1 = new TableAccessTarget("cat1", "public", "orders", "a", "cat1.public.orders");
        var target2 = new TableAccessTarget("cat2", "public", "orders", "b", "cat2.public.orders");
        var sqlMeta = CreateMetadata([target1, target2]);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await router.RouteAsync(sqlMeta, null, new TenantId(Tenant), isDml: false));

        ex.Message.ShouldContain("Cross-catalog queries across multiple data sources are not supported in WebSQL.");
    }

    [Fact]
    public async Task CrossSource_Dml_IsRejected()
    {
        var meta1 = CreateTable("cat1", "orders", DataSourceType.Sql, "db1");
        var meta2 = CreateTable("cat2", "orders", DataSourceType.Sql, "db2");

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(new TableIdentifier("cat1", "public", "orders"), Arg.Any<CancellationToken>()).Returns(meta1);
        repo.GetTableMetadataAsync(new TableIdentifier("cat2", "public", "orders"), Arg.Any<CancellationToken>()).Returns(meta2);

        var router = new CrossSourceQueryRouter(
            repo,
            new CrossSourceOptions { Enabled = true },
            new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["cat1", "cat2"] });

        var target1 = new TableAccessTarget("cat1", "public", "orders", "a", "cat1.public.orders");
        var target2 = new TableAccessTarget("cat2", "public", "orders", "b", "cat2.public.orders");
        var sqlMeta = CreateMetadata([target1, target2], SqlStatementType.Insert);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await router.RouteAsync(sqlMeta, null, new TenantId(Tenant), isDml: true));

        ex.Message.ShouldContain("DML");
    }

    [Fact]
    public async Task RequestedDataSource_Mismatch_IsRejected()
    {
        var meta1 = CreateTable("cat1", "orders", DataSourceType.Sql, "cat1");
        var meta2 = CreateTable("cat2", "orders", DataSourceType.Sql, "cat2");

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(new TableIdentifier("cat1", "public", "orders"), Arg.Any<CancellationToken>()).Returns(meta1);
        repo.GetTableMetadataAsync(new TableIdentifier("cat2", "public", "orders"), Arg.Any<CancellationToken>()).Returns(meta2);

        var router = new CrossSourceQueryRouter(
            repo,
            new CrossSourceOptions { Enabled = true },
            new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["cat1", "cat2"] });

        var target1 = new TableAccessTarget("cat1", "public", "orders", "a", "cat1.public.orders");
        var target2 = new TableAccessTarget("cat2", "public", "orders", "b", "cat2.public.orders");
        var sqlMeta = CreateMetadata([target1, target2]);

        // Client requested specific dataSource "cat1", but query references "cat2"
        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await router.RouteAsync(sqlMeta, "cat1", new TenantId(Tenant), isDml: false));

        ex.Message.ShouldContain("does not match requested data source");
    }
}
