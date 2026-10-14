namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Analysis;
using Xunit;

/// <summary>
/// SR-H-06: WebSql.TenantDataSourceAllowlist / WebSql.AllowedDataSources apply to every catalog of a cross-source,
/// HTTP-declarative and OLAP-staging read, before any upstream (metadata or connector) access.
/// </summary>
public sealed class SrH06DataSourceAllowlistTests
{
    private const string Tenant = "acme";

    private static TableMetadata Table(string domain, string table, DataSourceType type) => new()
    {
        Identifier = new TableIdentifier(domain, "public", table),
        Table = new Table
        {
            TableName = table,
            SchemaName = "public",
            SourceType = type == DataSourceType.Sql ? "PostgreSql" : "Http",
            DataSourceType = type,
            SourceName = domain,
            IsActive = true
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static SqlQueryMetadata Query(params TableAccessTarget[] tables) => new(
        StatementType: SqlStatementType.Select,
        ReferencedTables: tables,
        ProjectedColumns: ["id"],
        JoinCount: tables.Length - 1,
        MaxSubqueryDepth: 0,
        HasExplicitLimit: false,
        ExplicitLimitValue: null);

    private static TableAccessTarget Target(string catalog, string table) =>
        new(catalog, "public", table, null, $"{catalog}.public.{table}");

    private static (CrossSourceQueryRouter Router, ITableMetadataRepository Repo) CreateRouter(WebSqlOptions webSql)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Table(ci.Arg<TableIdentifier>().Domain, ci.Arg<TableIdentifier>().TableName,
                ci.Arg<TableIdentifier>().Domain == "logistics" ? DataSourceType.HttpDeclarative : DataSourceType.Sql));
        return (new CrossSourceQueryRouter(repo, new CrossSourceOptions { Enabled = true }, webSql), repo);
    }

    [Fact]
    public async Task SR_H_06_CrossSource_CatalogOutsideTenantAllowlist_IsDeniedBeforeAnyMetadataRead()
    {
        var (router, repo) = CreateRouter(new WebSqlOptions
        {
            AllowedDataSources = ["acme_db", "globex_db"],
            TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [Tenant] = ["acme_db"] }
        });

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            router.RouteAsync(Query(Target("acme_db", "orders"), Target("globex_db", "orders")), null, new TenantId(Tenant), isDml: false));

        ex.Message.ShouldBe("The requested data source is not enabled for this tenant.");
        await repo.DidNotReceive().GetTableMetadataAsync(new TableIdentifier("globex_db", "public", "orders"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SR_H_06_CrossSource_CatalogOutsideGlobalAllowlist_IsDenied()
    {
        var (router, _) = CreateRouter(new WebSqlOptions { AllowedDataSources = ["acme_db"] });

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            router.RouteAsync(Query(Target("acme_db", "orders"), Target("globex_db", "orders")), null, new TenantId(Tenant), isDml: false));

        ex.Message.ShouldBe("The requested data source is not enabled for WebSQL.");
    }

    [Fact]
    public async Task SR_H_06_HttpDeclarative_CatalogOutsideTenantAllowlist_IsDenied()
    {
        var (router, _) = CreateRouter(new WebSqlOptions
        {
            AllowedDataSources = ["acme_db", "logistics"],
            TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [Tenant] = ["acme_db"] }
        });

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            router.RouteAsync(Query(Target("acme_db", "orders"), Target("logistics", "shipments")), null, new TenantId(Tenant), isDml: false));
    }

    [Fact]
    public async Task SR_H_06_CrossSource_AllCatalogsAllowedForTenant_IsRouted()
    {
        var (router, _) = CreateRouter(new WebSqlOptions
        {
            AllowedDataSources = ["acme_db", "globex_db"],
            TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [Tenant] = ["acme_db", "globex_db"] }
        });

        var decision = await router.RouteAsync(Query(Target("acme_db", "orders"), Target("globex_db", "orders")), null, new TenantId(Tenant), isDml: false);

        decision.SourceClass.ShouldBe(QuerySourceClass.CrossSource);
    }

    private static async Task<(int Status, IAutherisConnectorRegistry Registry, ICrossDomainAccessResolver Resolver)> RunOlapAsync(WebSqlOptions webSql)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"), new Claim("tenant_id", Tenant)], "Bearer"));
        httpContext.RequestServices = new ServiceCollection().AddSingleton(Substitute.For<IAuditLogRepository>()).BuildServiceProvider();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { sql = "SELECT 1", tableNames = new[] { "globex_db.public.orders" } })));

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Table("globex_db", "orders", DataSourceType.Sql));
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        var resolver = Substitute.For<ICrossDomainAccessResolver>();
        var engine = Substitute.For<IDuckDbOlapEngine>();

        await DuckDbOlapEndpoints.HandleOlapQueryAsync(
            httpContext, engine, metadataRepo, registry, resolver, Substitute.For<IColumnMaskingProvider>(),
            Options.Create(new GatewayOptions { WebSql = webSql, DuckDbOlap = new DuckDbOlapOptions { Enabled = true, MaxStagedRowsPerTable = 100 } }),
            NullLoggerFactory.Instance);

        return (httpContext.Response.StatusCode, registry, resolver);
    }

    [Fact]
    public async Task SR_H_06_Olap_TableOutsideTenantAllowlist_IsForbiddenBeforeAccessDecisionOrStaging()
    {
        var (status, registry, resolver) = await RunOlapAsync(new WebSqlOptions
        {
            TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [Tenant] = ["acme_db"] }
        });

        status.ShouldBe(StatusCodes.Status403Forbidden);
        registry.ReceivedCalls().ShouldBeEmpty();
        resolver.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task SR_H_06_Olap_TableOutsideConfiguredGlobalAllowlist_IsForbidden()
    {
        var (status, registry, resolver) = await RunOlapAsync(new WebSqlOptions { AllowedDataSources = ["acme_db"] });

        status.ShouldBe(StatusCodes.Status403Forbidden);
        registry.ReceivedCalls().ShouldBeEmpty();
        resolver.ReceivedCalls().ShouldBeEmpty();
    }
}
