namespace Autheris.Tests.Unit.Analytics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Audit;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Dbt;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#112 P2 WP-4 (T-4.1..T-4.5, SEC-112-16 d, SEC-112-18, A1-3, A1-6): tenant-explicit, typed-where and kinds of the metric service.</summary>
public sealed class SemanticMetricQueryServiceP2Tests
{
    private static readonly TenantId TenantA = new("tenant-a");

    private sealed class Rig
    {
        public required SemanticMetricQueryService Service { get; init; }
        public required ISemanticCatalogService Catalog { get; init; }
        public required IGovernedAggregationService Aggregation { get; init; }
        public AggregationQuery? Captured { get; set; }
    }

    private static ClaimsPrincipal Principal(string? tenant = "tenant-a", bool sid = true, string role = "Analyst")
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "u"), new(ClaimTypes.Role, role) };
        if (sid) claims.Add(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-U1"));
        if (tenant != null) claims.Add(new Claim("tenant_id", tenant));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static DbtMetric Metric(string name, string? filter = null, string measure = "order_total") =>
        new(name, "simple", "d", name, filter, "{ \"measure\": \"x\" }", ["status"], [measure],
            new Dictionary<string, string> { ["model"] = "orders" }, Unit: "EUR");

    private static async Task<Rig> NewRigAsync(
        IEnumerable<DbtMetric>? metrics = null,
        bool allow = true,
        TenancyMode mode = TenancyMode.Multi,
        IEnumerable<DbtMeasure>? measures = null,
        ISemanticCatalogService? catalogOverride = null)
    {
        var options = SemanticCatalogTestKit.Mode(mode);
        var catalog = catalogOverride ?? new InMemorySemanticCatalog(options);
        var model = new DbtSemanticModel(
            "orders", "d", "analytics.public.orders", [new DbtEntity("id", "primary")],
            (measures ?? [new DbtMeasure("order_total", "sum", "amount", null), new DbtMeasure("order_count", "count", "1", null)]).ToList(),
            [new DbtDimension("status", "categorical", null)]);
        if (catalogOverride is null)
        {
            await catalog.RegisterManifestAsync(new DbtSemanticManifest([model], (metrics ?? [Metric("revenue")]).ToList(), "tv-1"), TenantA);
        }

        var table = SemanticManifestValidatorTests.Orders();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(table.Identifier, Arg.Any<CancellationToken>()).Returns(table);
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([table]);

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        var decision = allow
            ? TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear, ["status"] = ColumnAccessLevel.Clear, ["amount"] = ColumnAccessLevel.Clear
            })
            : TableAccessDecision.Denied(table.Identifier, "no consent");
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(decision);

        var aggregation = Substitute.For<IGovernedAggregationService>();
        var rig = new Rig
        {
            Service = new SemanticMetricQueryService(
                catalog, aggregation, repo, consentRepository: null, resolutionService: null, pdp: pdp, accessResolver: null,
                auditWriter: Substitute.For<IAuditWriter>(), gatewayOptions: options),
            Catalog = catalog,
            Aggregation = aggregation
        };
        aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Do<AggregationQuery>(q => rig.Captured = q), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var q = ci.Arg<AggregationQuery>();
                var aggs = q.Aggregates!.ToDictionary(a => a.EffectiveAlias, _ => (object?)1m, StringComparer.OrdinalIgnoreCase);
                var keys = (q.GroupBy ?? []).ToDictionary(g => g, _ => (object?)"x", StringComparer.OrdinalIgnoreCase);
                return new AggregationResult(
                    q.Table, [new AggregationGroupRow(keys, 5, aggs)], null,
                    new AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, false));
            });
        return rig;
    }

    [Fact]
    public async Task T_4_1_TenantOfThePrincipal_IsPassedToEveryCatalogCall()
    {
        var catalog = Substitute.For<ISemanticCatalogService>();
        var rig = await NewRigAsync(catalogOverride: catalog);
        catalog.GetMetricAsync("revenue", Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns(Metric("revenue"));
        catalog.GetManifestVersionAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns("tv-1");
        catalog.ListSemanticModelsAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns(
        [
            new DbtSemanticModel("orders", "d", "analytics.public.orders", [], [new DbtMeasure("order_total", "sum", "amount", null)], [new DbtDimension("status", "categorical", null)])
        ]);

        await rig.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["revenue"]));

        await catalog.Received().GetMetricAsync("revenue", TenantA, Arg.Any<CancellationToken>());
        await catalog.Received().ListSemanticModelsAsync(TenantA, Arg.Any<CancellationToken>());
        await catalog.Received().GetManifestVersionAsync(TenantA, Arg.Any<CancellationToken>());
        await catalog.DidNotReceive().GetMetricAsync(Arg.Any<string>(), Arg.Is<TenantId>(t => t != TenantA), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task T_4_2_TypedWhere_BuildsAClauseWithBoundParameters_AndKeepsTheMetricFilter()
    {
        var rig = await NewRigAsync([Metric("revenue", filter: "amount > 5")]);
        var typed = new TreeComparison("STATUS", TreeFilterOperator.Eq, "completed") { DeclaredKind = ColumnValueKind.String };

        var result = await rig.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["revenue"]) { TypedWhere = typed });

        var where = rig.Captured!.Where!;
        where.SqlPredicate.ShouldContain("amount > 5");
        where.SqlPredicate.ShouldContain(TreeFilterClauseBuilder.ParameterPrefix);
        where.SqlPredicate.ShouldNotContain("completed");
        where.Parameters.Values.ShouldContain("completed");
        where.ReferencedColumns.ShouldContain("status");
        where.ReferencedColumns.ShouldContain("amount");
        result.ManifestVersion.ShouldBe("tv-1");
    }

    [Fact]
    public async Task T_4_2_TypedWhere_AndTextWhere_AreMutuallyExclusive()
    {
        var rig = await NewRigAsync();

        await Should.ThrowAsync<ArgumentException>(() => rig.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(
            ["revenue"], Where: new TableFilterClause("status = 'x'", new Dictionary<string, object?>(), ["status"]))
        { TypedWhere = new TreeComparison("status", TreeFilterOperator.Eq, "x") }));
        rig.Captured.ShouldBeNull();
    }

    [Fact]
    public async Task T_4_3_StringFilterOnIntColumn_IsMetricFilterInvalid_BeforeTheEngine()
    {
        var rig = await NewRigAsync();
        var typed = new TreeComparison("id", TreeFilterOperator.Eq, "5") { DeclaredKind = ColumnValueKind.String };

        var ex = await Should.ThrowAsync<MetricFilterInvalidException>(() =>
            rig.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["revenue"]) { TypedWhere = typed }));

        ex.ErrorCode.ShouldBe("METRIC_FILTER_INVALID");
        ex.Message.ShouldNotContain("id");
        ex.Message.ShouldNotContain("5");
        rig.Captured.ShouldBeNull();
    }

    [Fact]
    public async Task T_4_3_UnknownDimension_IsColumnUnavailable()
    {
        var rig = await NewRigAsync();
        var typed = new TreeComparison("no_such_column", TreeFilterOperator.Eq, "x") { DeclaredKind = ColumnValueKind.String };

        await Should.ThrowAsync<AggregationColumnUnavailableException>(() =>
            rig.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["revenue"]) { TypedWhere = typed }));
        rig.Captured.ShouldBeNull();
    }

    [Fact]
    public async Task T_4_4_ValueKinds_CoverSimpleRatioDerivedAndCountMetrics()
    {
        var ratio = new DbtMetric("avg_value", "ratio", null, null, null, null, ["status"], ["order_total", "order_count"], new Dictionary<string, string> { ["model"] = "orders" });
        var derived = new DbtMetric("net", "derived", null, null, null, "order_total - order_count", ["status"], [], new Dictionary<string, string> { ["model"] = "orders" });
        var rig = await NewRigAsync([Metric("revenue"), Metric("orders", measure: "order_count"), ratio, derived]);

        var result = await rig.Service.QueryMetricsAsync(
            Principal(), new MetricQueryRequest(["revenue", "orders", "avg_value", "net"], GroupBy: ["status"]));

        result.MetricValueKinds["revenue"].ShouldBe(AggregateValueKind.Decimal);
        result.MetricValueKinds["orders"].ShouldBe(AggregateValueKind.Int);
        result.MetricValueKinds["avg_value"].ShouldBe(AggregateValueKind.Decimal);
        result.MetricValueKinds["net"].ShouldBe(AggregateValueKind.Decimal);
        result.DimensionValueKinds["status"].ShouldBe(AggregateValueKind.String);
    }

    [Fact]
    public async Task T_4_4_SumOverIntColumn_IsInt_AndMinOverStringIsString()
    {
        var rig = await NewRigAsync(
            [Metric("max_status", measure: "max_status_m"), Metric("sum_ids", measure: "id_sum")],
            measures: [new DbtMeasure("max_status_m", "max", "status", null), new DbtMeasure("id_sum", "sum", "id", null)]);

        var result = await rig.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["max_status", "sum_ids"]));

        result.MetricValueKinds["max_status"].ShouldBe(AggregateValueKind.String);
        result.MetricValueKinds["sum_ids"].ShouldBe(AggregateValueKind.Int);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_4_5_UnknownMetric_NoConsent_AndClaimlessCaller_AreUniform()
    {
        var granted = await NewRigAsync();
        var denied = await NewRigAsync(allow: false);

        var unknown = await Should.ThrowAsync<KeyNotFoundException>(() => granted.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["nope"])));
        var foreignTenant = await Should.ThrowAsync<KeyNotFoundException>(() => granted.Service.QueryMetricsAsync(Principal("tenant-b"), new MetricQueryRequest(["revenue"])));
        var claimless = await Should.ThrowAsync<KeyNotFoundException>(() => granted.Service.QueryMetricsAsync(Principal(tenant: null), new MetricQueryRequest(["nope"])));
        var noConsent = await Should.ThrowAsync<GatewayForbiddenException>(() => denied.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["revenue"])));

        foreignTenant.Message.ShouldBe(unknown.Message.Replace("nope", "revenue"));
        claimless.Message.ShouldBe(unknown.Message);
        noConsent.Message.ShouldBe("Access denied.");
        granted.Captured.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SemanticStore_NoSid_Multi_NoAccess_ForTheMetricService()
    {
        var rig = await NewRigAsync();

        await Should.ThrowAsync<GatewayUnauthorizedException>(() => rig.Service.QueryMetricsAsync(Principal(sid: false), new MetricQueryRequest(["revenue"])));
        rig.Captured.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MetricDefinitionSummary_AllowListOnly_NoFilterTypeParamsMeta()
    {
        var rig = await NewRigAsync([Metric("revenue", filter: "status = 'secret_filter'")]);

        var result = await rig.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["revenue"]));

        var json = JsonSerializer.Serialize(result.MetricDefinitions);
        json.ShouldContain("revenue");
        json.ShouldContain("EUR");
        json.ShouldNotContain("secret_filter");
        json.ShouldNotContain("measure");
        json.ShouldNotContain("model");
        json.ShouldNotContain("filter", Case.Insensitive);
        typeof(MetricDefinitionSummary).GetProperties().Select(p => p.Name)
            .ShouldBe(["Name", "Type", "Label", "Description", "Unit"], ignoreOrder: true);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task StoredTemplate_ColumnRemovedAfterImport_FailsClosedAtQueryTime()
    {
        // The manifest was valid when imported; the base table no longer has the column the template references.
        var rig = await NewRigAsync([Metric("revenue", filter: "removed_column = 'x'")]);

        await Should.ThrowAsync<AggregationColumnUnavailableException>(() =>
            rig.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["revenue"])));
        rig.Captured.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task StoredTemplate_DisallowedFunction_FailsClosedAtQueryTime()
    {
        var rig = await NewRigAsync([Metric("revenue", filter: "xp_cmdshell(status) = 'x'")]);

        await Should.ThrowAsync<AggregationColumnUnavailableException>(() =>
            rig.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["revenue"])));
    }

    [Fact]
    public async Task ManifestVersion_OfTheResult_IsTheCallersTenantVersion()
    {
        var rig = await NewRigAsync([Metric("revenue")]);
        await rig.Catalog.RegisterManifestAsync(new DbtSemanticManifest([], [], "tenant-b-99"), new TenantId("tenant-b"));

        var result = await rig.Service.QueryMetricsAsync(Principal(), new MetricQueryRequest(["revenue"]));

        result.ManifestVersion.ShouldBe("tv-1");
    }
}
