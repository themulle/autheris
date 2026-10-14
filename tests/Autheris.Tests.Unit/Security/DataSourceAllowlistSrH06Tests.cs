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
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Analysis;
using Xunit;

/// <summary>
/// SR-H-06 / SEC-HIGH-13: <c>WebSql.TenantDataSourceAllowlist</c> and <c>WebSql.AllowedDataSources</c> apply to the
/// cross-source router, HTTP-declarative tables and OLAP staging, not only to the single-catalog path.
/// </summary>
public sealed class DataSourceAllowlistSrH06Tests
{
    private const string TenantA = "tenant_a";

    private static TableMetadata Table(string domain, string table, DataSourceType type = DataSourceType.Sql) => new()
    {
        Identifier = new TableIdentifier(domain, "public", table),
        Table = new Table
        {
            TableName = table,
            SchemaName = "public",
            SourceType = type == DataSourceType.Sql ? "PostgreSql" : "Http",
            DataSourceType = type,
            SourceName = domain,
            IsActive = true,
            HttpEndpoint = type == DataSourceType.HttpDeclarative
                ? new HttpEndpointDescriptor { BaseUrl = "https://api.internal", PathTemplate = "/x", CompleteResponse = true }
                : null
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static SqlQueryMetadata Query(params TableAccessTarget[] tables) =>
        new(SqlStatementType.Select, tables, ["id"], tables.Length - 1, 0, false, null);

    private static TableAccessTarget Target(string catalog, string table) =>
        new(catalog, "public", table, null, $"{catalog}.public.{table}");

    private static CrossSourceQueryRouter Router(WebSqlOptions web, params TableMetadata[] metas)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        foreach (var m in metas)
        {
            repo.GetTableMetadataAsync(m.Identifier, Arg.Any<CancellationToken>()).Returns(m);
        }

        return new CrossSourceQueryRouter(repo, new CrossSourceOptions { Enabled = true }, web);
    }

    private static WebSqlOptions TenantAllowlist(params string[] allowed) => new()
    {
        DefaultDataSourceName = "default",
        AllowedDataSources = ["ds1", "ds2"],
        TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [TenantA] = [.. allowed]
        }
    };

    [Fact]
    public async Task SR_H_06_CrossSource_TouchingNonAllowedDataSource_IsDenied()
    {
        var router = Router(TenantAllowlist("ds1"), Table("ds1", "a"), Table("ds2", "b"));

        await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await router.RouteAsync(Query(Target("ds1", "a"), Target("ds2", "b")), null, new TenantId(TenantA), isDml: false));
    }

    [Fact]
    public async Task SR_H_06_CrossSource_AllAllowed_StillRoutes()
    {
        var router = Router(TenantAllowlist("ds1", "ds2"), Table("ds1", "a"), Table("ds2", "b"));

        var decision = await router.RouteAsync(Query(Target("ds1", "a"), Target("ds2", "b")), null, new TenantId(TenantA), isDml: false);

        decision.SourceClass.ShouldBe(QuerySourceClass.CrossSource);
    }

    [Fact]
    public async Task SR_H_06_CrossSource_OtherTenantWithoutEntry_KeepsGlobalAllowlist()
    {
        var router = Router(TenantAllowlist("ds1"), Table("ds1", "a"), Table("ds2", "b"));

        var decision = await router.RouteAsync(Query(Target("ds1", "a"), Target("ds2", "b")), null, new TenantId("tenant_b"), isDml: false);

        decision.SourceClass.ShouldBe(QuerySourceClass.CrossSource);
    }

    [Fact]
    public async Task SR_H_06_CrossSource_GlobalAllowlist_Enforced_UnknownCatalogFailsClosed()
    {
        var web = new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["ds1"] };
        var router = Router(web, Table("ds1", "a"), Table("ds9", "z"));

        await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await router.RouteAsync(Query(Target("ds1", "a"), Target("ds9", "z")), null, new TenantId(TenantA), isDml: false));
    }

    [Fact]
    public async Task SR_H_06_HttpDeclarativeTable_OnNonAllowedSource_IsDenied()
    {
        var router = Router(TenantAllowlist("ds1"), Table("ds1", "a"), Table("ds2", "api", DataSourceType.HttpDeclarative));

        await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await router.RouteAsync(Query(Target("ds1", "a"), Target("ds2", "api")), null, new TenantId(TenantA), isDml: false));
    }

    [Fact]
    public async Task SR_H_06_HttpDeclarativeTable_OnAllowedSource_StillRoutes()
    {
        var router = Router(TenantAllowlist("ds1", "ds2"), Table("ds1", "a"), Table("ds2", "api", DataSourceType.HttpDeclarative));

        var decision = await router.RouteAsync(Query(Target("ds1", "a"), Target("ds2", "api")), null, new TenantId(TenantA), isDml: false);

        decision.SourceClass.ShouldBe(QuerySourceClass.CrossSource);
    }

    private static StagingTableRequest StagingRequest(TableMetadata meta) => new(
        Target(meta.Identifier.Domain, meta.Identifier.TableName),
        meta,
        TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
        "s0",
        ["id"]);

    private static (FederatedStagingService Service, IAutherisConnectorRegistry Registry) Staging(WebSqlOptions web)
    {
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        var service = new FederatedStagingService(
            registry,
            Substitute.For<IColumnMaskingProvider>(),
            Substitute.For<IAuditLogRepository>(),
            Options.Create(new GatewayOptions { WebSql = web }),
            NullLogger<FederatedStagingService>.Instance);
        return (service, registry);
    }

    private static readonly FederationBudget Budget = new(5, 1000, 5000, 32 * 1024 * 1024, 128 * 1024 * 1024, 30, 4);

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1")], "Test"));

    [Theory]
    [InlineData(DataSourceType.Sql)]
    [InlineData(DataSourceType.HttpDeclarative)]
    public async Task SR_H_06_FederatedStaging_NonAllowedSource_IsDenied_BeforeAnyRead(DataSourceType type)
    {
        var (service, registry) = Staging(TenantAllowlist("ds1"));

        await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await service.StageAsync([StagingRequest(Table("ds2", "b", type))], User(), new TenantId(TenantA), Budget));

        registry.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task SR_H_06_FederatedStaging_AllowedSource_PassesAllowlist()
    {
        var (service, _) = Staging(TenantAllowlist("ds1"));

        // Passes the allowlist and stops at the read phase (no connector registered in the stub registry).
        var ex = await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewaySecurityException>(async () =>
            await service.StageAsync([StagingRequest(Table("ds1", "a"))], User(), new TenantId(TenantA), Budget));
        ex.ErrorCode.ShouldBe("CONNECTOR_UNAVAILABLE");
    }

    private static async Task<(int Status, IAutherisConnectorRegistry Registry)> RunOlapAsync(WebSqlOptions web, string domain)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tid", TenantA), new Claim("sub", "user-1"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER")], "test"));
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            sql = "SELECT 1",
            tableNames = new[] { $"{domain}.public.orders" }
        })));

        var meta = Table(domain, "orders");
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(meta);
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        var access = Substitute.For<ICrossDomainAccessResolver>();
        access.ResolveAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var options = Options.Create(new GatewayOptions
        {
            WebSql = web,
            DuckDbOlap = new DuckDbOlapOptions { Enabled = true }
        });

        await DuckDbOlapEndpoints.HandleOlapQueryAsync(
            httpContext, Substitute.For<IDuckDbOlapEngine>(), repo, registry, access,
            Substitute.For<IColumnMaskingProvider>(), options, NullLoggerFactory.Instance);

        return (httpContext.Response.StatusCode, registry);
    }

    [Fact]
    public async Task SR_H_06_OlapStaging_NonAllowedSource_Returns403_WithoutConnectorLookup()
    {
        var (status, registry) = await RunOlapAsync(TenantAllowlist("ds1"), "ds2");

        status.ShouldBe(StatusCodes.Status403Forbidden);
        registry.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task SR_H_06_OlapStaging_GloballyNotAllowedSource_Returns403()
    {
        var web = new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["ds1"] };

        var (status, _) = await RunOlapAsync(web, "ds2");

        status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task SR_H_06_OlapStaging_AllowedSource_PassesAllowlist()
    {
        var (status, registry) = await RunOlapAsync(TenantAllowlist("ds1"), "ds1");

        // Passes the allowlist; the stub registry has no connector, so the later connector gate answers 403 after a lookup.
        registry.ReceivedCalls().ShouldNotBeEmpty();
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }
}
