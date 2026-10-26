namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Dbt;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#112 T-3.6 / T-3.7 / T-S-11 / A1-3 / A1-6: tenant-explicit import and list routes.</summary>
public sealed class DbtSemanticManifestEndpointTests
{
    private const string Manifest = """
    { "version": "v5", "tenant_id": "tenant-b",
      "semantic_models": [ { "name": "orders", "description": "d", "node_relation": { "relation_name": "analytics.public.orders" },
        "entities": [ { "name": "id", "type": "primary" } ],
        "measures": [ { "name": "order_total", "agg": "sum", "expr": "amount" } ],
        "dimensions": [ { "name": "status", "type": "categorical" } ] } ],
      "metrics": [ { "name": "revenue", "type": "simple", "label": "Revenue", "filter": "status = 'secret_filter'", "dimensions": ["status"],
                     "type_params": { "measure": "order_total" }, "meta": { "unit": "EUR" } } ] }
    """;

    private static ClaimsPrincipal Principal(string? tenant, string role, bool sid = true)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "u-" + role), new(ClaimTypes.Role, role) };
        if (sid) claims.Add(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-" + role));
        if (tenant != null) claims.Add(new Claim("tenant_id", tenant));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static DefaultHttpContext Context(ClaimsPrincipal user, string? body = null)
    {
        var ctx = new DefaultHttpContext { User = user };
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        if (body != null)
        {
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            ctx.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        }

        return ctx;
    }

    private static async Task<(int Status, string Body)> RunAsync(IResult result, DefaultHttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    private sealed class Host
    {
        public required InMemorySemanticCatalog Catalog { get; init; }
        public required DbtSemanticManifestIngestionService Ingestion { get; init; }
        public required SemanticTenancy Tenancy { get; init; }
    }

    private static Host NewHost(TenancyMode mode)
    {
        var options = SemanticCatalogTestKit.Mode(mode);
        var catalog = new InMemorySemanticCatalog(options);
        var orders = SemanticManifestValidatorTests.Orders();
        var repository = Substitute.For<ITableMetadataRepository>();
        repository.GetTableMetadataAsync(orders.Identifier, Arg.Any<CancellationToken>()).Returns(orders);
        repository.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([orders]);
        repository.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<TableMetadata>()));
        var consents = Substitute.For<IConsentRepository>();
        consents.GetTablesWithConsentsForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns([orders.Identifier]);
        return new Host
        {
            Catalog = catalog,
            Tenancy = new SemanticTenancy(mode.Owner()),
            Ingestion = new DbtSemanticManifestIngestionService(
                catalog, NullLogger<DbtSemanticManifestIngestionService>.Instance, repository, gatewayOptions: options, consentRepository: consents)
        };
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_S_11_TenantAImports_TenantBSeesNoMetric_ImportTenantComesFromTheToken()
    {
        var host = NewHost(TenancyMode.Multi);
        var admin = Context(Principal("tenant-a", "GovernanceAdmin"), Manifest);

        var import = await RunAsync(await DbtEndpoints.HandleImportSemanticManifestAsync(admin, host.Ingestion, host.Tenancy), admin);

        import.Status.ShouldBe(200);
        (await host.Catalog.GetMetricAsync("revenue", new TenantId("tenant-a"))).ShouldNotBeNull();
        // The body claims tenant-b; it is ignored.
        (await host.Catalog.GetMetricAsync("revenue", new TenantId("tenant-b"))).ShouldBeNull();

        var listB = Context(Principal("tenant-b", "Analyst"));
        var listed = await RunAsync(await DbtEndpoints.HandleListDbtMetricsAsync(listB, host.Catalog, host.Tenancy), listB);
        listed.Body.ShouldBe("[]");

        var restB = Context(Principal("tenant-b", "Analyst"));
        var rest = await RunAsync(await AnalyticsEndpoints.HandleListMetricsAsync(restB, host.Catalog, host.Tenancy), restB);
        using var doc = JsonDocument.Parse(rest.Body);
        doc.RootElement.GetProperty("total").GetInt32().ShouldBe(0);
        doc.RootElement.GetProperty("manifestVersion").GetString().ShouldBe("1.0");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SemanticStore_MultiMode_ClaimlessImport_403_AndEmptyList()
    {
        var host = NewHost(TenancyMode.Multi);
        var claimless = Context(Principal(null, "GovernanceAdmin"), Manifest);

        var import = await RunAsync(await DbtEndpoints.HandleImportSemanticManifestAsync(claimless, host.Ingestion, host.Tenancy), claimless);

        import.Status.ShouldBe(403);
        (await host.Catalog.ListMetricsAsync(null, TenantId.LegacySingleTenant)).ShouldBeEmpty();
        var list = Context(Principal(null, "Analyst"));
        (await RunAsync(await DbtEndpoints.HandleListDbtMetricsAsync(list, host.Catalog, host.Tenancy), list)).Body.ShouldBe("[]");
    }

    [Theory]
    [InlineData(TenancyMode.Single)]
    [InlineData(TenancyMode.Multi)]
    [Trait("Category", "Security")]
    public async Task SemanticStore_NoSid_Import403_AndEmptyList_InBothModes(TenancyMode mode)
    {
        var host = NewHost(mode);
        var noSid = Context(Principal("tenant-a", "GovernanceAdmin", sid: false), Manifest);

        (await RunAsync(await DbtEndpoints.HandleImportSemanticManifestAsync(noSid, host.Ingestion, host.Tenancy), noSid)).Status.ShouldBe(403);

        var list = Context(Principal("tenant-a", "Analyst", sid: false));
        (await RunAsync(await DbtEndpoints.HandleListDbtMetricsAsync(list, host.Catalog, host.Tenancy), list)).Body.ShouldBe("[]");
    }

    [Fact]
    public async Task SingleMode_DefaultTenantCallerImports_OtherTenantsDoNotSeeTheManifest()
    {
        var host = NewHost(TenancyMode.Single);
        var admin = Context(Principal(null, "GovernanceAdmin"), Manifest);

        (await RunAsync(await DbtEndpoints.HandleImportSemanticManifestAsync(admin, host.Ingestion, host.Tenancy), admin)).Status.ShouldBe(200);

        var analyst = Context(Principal("tenant-b", "Analyst"));
        var list = await RunAsync(await DbtEndpoints.HandleListDbtMetricsAsync(analyst, host.Catalog, host.Tenancy), analyst);
        list.Body.ShouldNotContain("revenue");
        var sameTenant = Context(Principal(null, "Analyst"));
        (await RunAsync(await DbtEndpoints.HandleListDbtMetricsAsync(sameTenant, host.Catalog, host.Tenancy), sameTenant)).Body.ShouldContain("revenue");
    }

    [Fact]
    public async Task T_3_7_NonAdmin_IsForbidden()
    {
        var host = NewHost(TenancyMode.Multi);
        var analyst = Context(Principal("tenant-a", "Analyst"), Manifest);

        (await RunAsync(await DbtEndpoints.HandleImportSemanticManifestAsync(analyst, host.Ingestion, host.Tenancy), analyst)).Status.ShouldBe(403);
        (await host.Catalog.ListMetricsAsync(null, new TenantId("tenant-a"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidManifest_Is400_Partial_Is500_Forbidden_Is403()
    {
        var host = NewHost(TenancyMode.Multi);
        var ingestion = Substitute.For<IDbtSemanticManifestIngestionService>();

        ingestion.ParseAndIngestAsync(Arg.Any<string>(), Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbtSemanticManifest>>(_ => throw new SemanticManifestInvalidException(["TEMPLATE_INVALID"]));
        var c1 = Context(Principal("tenant-a", "GovernanceAdmin"), Manifest);
        var r1 = await RunAsync(await DbtEndpoints.HandleImportSemanticManifestAsync(c1, ingestion, host.Tenancy), c1);
        r1.Status.ShouldBe(400);
        r1.Body.ShouldContain("DBT_MANIFEST_INVALID");
        r1.Body.ShouldNotContain("TEMPLATE_INVALID");

        ingestion.ParseAndIngestAsync(Arg.Any<string>(), Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbtSemanticManifest>>(_ => throw new SemanticManifestPartialException(new InvalidOperationException("x")));
        var c2 = Context(Principal("tenant-a", "GovernanceAdmin"), Manifest);
        var r2 = await RunAsync(await DbtEndpoints.HandleImportSemanticManifestAsync(c2, ingestion, host.Tenancy), c2);
        r2.Status.ShouldBe(500);
        r2.Body.ShouldContain("DBT_MANIFEST_PARTIAL");

        ingestion.ParseAndIngestAsync(Arg.Any<string>(), Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbtSemanticManifest>>(_ => throw new GatewayForbiddenException());
        var c3 = Context(Principal("tenant-a", "GovernanceAdmin"), Manifest);
        (await RunAsync(await DbtEndpoints.HandleImportSemanticManifestAsync(c3, ingestion, host.Tenancy), c3)).Status.ShouldBe(403);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RestMetricsList_NonAdmin_HasNoFilterTemplate_AdminKeepsIt()
    {
        var host = NewHost(TenancyMode.Multi);
        var import = Context(Principal("tenant-a", "GovernanceAdmin"), Manifest);
        await RunAsync(await DbtEndpoints.HandleImportSemanticManifestAsync(import, host.Ingestion, host.Tenancy), import);

        var analyst = Context(Principal("tenant-a", "Analyst"));
        var analystJson = (await RunAsync(await AnalyticsEndpoints.HandleListMetricsAsync(analyst, host.Catalog, host.Tenancy), analyst)).Body;
        var admin = Context(Principal("tenant-a", "GovernanceAdmin"));
        var adminJson = (await RunAsync(await AnalyticsEndpoints.HandleListMetricsAsync(admin, host.Catalog, host.Tenancy), admin)).Body;

        analystJson.ShouldContain("revenue");
        analystJson.ShouldContain("v5");
        analystJson.ShouldNotContain("secret_filter");
        adminJson.ShouldContain("secret_filter");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RestMetricsQuery_Result_HasNoFilterTemplate()
    {
        var host = NewHost(TenancyMode.Multi);
        var metric = new DbtMetric("revenue", "simple", "d", "Revenue", "status = 'secret_filter'", "{ \"measure\": \"x\" }", ["status"], ["order_total"],
            new Dictionary<string, string> { ["model"] = "internal_model" }, Unit: "EUR");
        var service = Substitute.For<Autheris.Application.Analytics.ISemanticMetricQueryService>();
        service.QueryMetricsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Autheris.Application.Analytics.MetricQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Autheris.Application.Analytics.MetricQueryResult(
                ["revenue"], "v5", [], null,
                new Autheris.Application.Analytics.AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, false),
                new Dictionary<string, MetricDefinitionSummary> { ["revenue"] = MetricDefinitionSummary.From(metric) }));
        var ctx = Context(Principal("tenant-a", "Analyst"));

        var result = await RunAsync(
            await AnalyticsEndpoints.HandleQueryMetricsAsync(new AnalyticsEndpoints.MetricQueryRequestDto(["revenue"]), ctx, service, default), ctx);

        result.Body.ShouldContain("revenue");
        result.Body.ShouldNotContain("secret_filter");
        result.Body.ShouldNotContain("internal_model");
        result.Body.ShouldNotContain("typeParams", Case.Insensitive);
        host.ShouldNotBeNull();
    }
}
