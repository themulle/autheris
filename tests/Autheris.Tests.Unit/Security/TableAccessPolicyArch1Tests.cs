namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Unit.TestDoubles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.DataSources.Connectors;
using Autheris.Application.DataSources.Connectors.CrossDomain;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Architecture 1 (SQL2-6, POL-6, API-10): REST/OData/GraphQL, WebSQL, procedures, OLAP and the unified PDP decide table
/// access through one <see cref="TableAccessPolicy"/>. Before, OLAP had no consent cache, WebSQL and procedures read consents without the decision cache, and each path merged Casbin
/// restrictions differently.
/// </summary>
public sealed class TableAccessPolicyArch1Tests
{
    private const string Tenant = "tenant_a";
    private static readonly TableIdentifier Table = new("sales", "public", "orders");

    private static ClaimsPrincipal User(params Claim[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-USER"), new("tenant_id", Tenant) };
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static TableMetadata Meta(bool isActive = true) => new()
    {
        Identifier = Table,
        Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSQL", IsActive = isActive },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    private static TableAccessDecision AllowAll() =>
        TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

    private static IConsentRepository NoConsents()
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        return repo;
    }

    private static IConsentResolutionService ResolvesTo(bool allowed)
    {
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(allowed ? AllowAll() : TableAccessDecision.Denied(Table, "no consent"));
        return resolution;
    }

    private static IConsentCacheService CacheWith(TableAccessDecision? cached)
    {
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(cached);
        return cache;
    }

    private static GatewayOptions WebSqlOn() => new()
    {
        WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 500 }
    };

    // ---- DuckDB OLAP resolver -------------------------------------------------------------------------------------

    [Fact]
    public async Task CrossDomainResolver_UsesTheConsentDecisionCache()
    {
        var resolver = new DefaultCrossDomainAccessResolver(
            NoConsents(), ResolvesTo(allowed: false), consentCache: CacheWith(AllowAll()));

        var decision = await resolver.ResolveAccessAsync(User(), Table, Meta(), new TenantId(Tenant));

        decision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task OlapEndpoint_ResolverIsTheSingleDecision_IsNotRejected()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.User = User();
        httpContext.RequestServices = new ServiceCollection().AddAuditWriterOver(Substitute.For<IAuditLogRepository>()).BuildServiceProvider();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sql = "SELECT 1", tableNames = new[] { "sales.public.orders" } })));

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Meta());

        var connector = Substitute.For<IAutherisConnector>();
        connector.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ConnectorSplit>());
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(Table, out Arg.Any<IAutherisConnector>()!).Returns(x => { x[1] = connector; return true; });

        // The resolver is the single decision; the endpoint must not add its own check.
        var resolver = Substitute.For<ICrossDomainAccessResolver>();
        resolver.ResolveAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(AllowAll());

        var engine = Substitute.For<IDuckDbOlapEngine>();
        engine.ExecuteOlapQueryAsync(Arg.Any<OlapQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OlapQueryResult([], [], 0, TimeSpan.Zero));

        await Autheris.Tests.Unit.TestDoubles.OlapEndpointTestHost.HandleAsync(
            httpContext, engine, metadataRepo, registry, resolver, Substitute.For<IColumnMaskingProvider>(), Options.Create(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { Enabled = true, MaxStagedRowsPerTable = 100 } }),
            NullLoggerFactory.Instance);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    // ---- WebSQL -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WebSql_UsesTheConsentDecisionCache()
    {
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(ci.Arg<TableIdentifier>().TableName == "orders" ? Meta() : null));
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Meta()]));

        var service = global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 500 } }), executionGuard: GovernedTestSupport.Guard,
            auditLogRepository: Substitute.For<IAuditLogRepository>(),
            consentResolution: ResolvesTo(allowed: false),
            tableRepository: tables, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(tables), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: NoConsents(),
            consentCache: CacheWith(AllowAll()));

        var sql = await service.RewriteSqlAsync("SELECT id FROM orders", User(), new TenantId(Tenant));

        sql.ShouldContain("orders");
    }

    // ---- the policy itself ---------------------------------------------------------------------------------------

    private static TableAccessPolicy Policy(
        IConsentResolutionService resolution,
        GatewayOptions? options = null,
        IConsentCacheService? cache = null) =>
        new(NoConsents(), resolution,
            cache ?? Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            options ?? new GatewayOptions(),
            Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance);

    private static TableAccessQuery Query(params Claim[] claims) =>
        TableAccessQuery.ForPrincipal(User(claims), new Sid("S-1-5-21-USER"), new TenantId(Tenant), Meta());

    [Fact]
    public async Task Policy_InactiveTable_IsDeniedDirectly()
    {
        var meta = Meta(isActive: false);
        var query = TableAccessQuery.ForPrincipal(User(), new Sid("S-1-5-21-USER"), new TenantId(Tenant), meta);
        var decision = await Policy(ResolvesTo(true)).DecideAsync(query, default);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("not active"));
    }
}
