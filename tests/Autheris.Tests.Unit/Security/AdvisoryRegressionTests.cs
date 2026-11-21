namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Common.Fakes;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Connectors;
using Autheris.Application.Federation.Services;
using Autheris.Application.Governance.Services;
using Autheris.Application.Olap;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Connectors;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// Regression cases for the P0 advisories of the security lane (sub-package 1): one case per fix-list bullet.
/// The bullets that need per-member column decisions inside the subtree planner are tracked by WP-SIMPL-21 / the Sql lane.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    private const string Spec = """{"openapi":"3.0.0","info":{"title":"T","version":"1"},"paths":{"/a":{"get":{"summary":"x"}}},"components":{"schemas":{"A":{"type":"object","properties":{"id":{"type":"string"}}}}}}""";

    private static readonly TenantId TenantA = new("tenant-a");

    private static HttpContext Http(string tenant, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "admin"), new("tenant_id", tenant) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) };
    }

    private static readonly ICatalogSearchEngine UnusedSearch = Substitute.For<ICatalogSearchEngine>();
    private static readonly IHttpClientFactory UnusedHttpFactory = Substitute.For<IHttpClientFactory>();

    private static CatalogDiscoveryService Discovery(IKeyVaultSecretProvider provider, IOpenApiIngestionService ingestion) =>
        new(new InMemoryTableMetadataRepository(), AuditWriterTestServices.Over(new InMemoryAuditLogRepository()),
            Options.Create(new GatewayOptions()), null,
            provider, ingestion, UnusedSearch, NullLogger<CatalogDiscoveryService>.Instance);

    private static RequestContext Ctx() => new(TenantA, new Sid("admin"), Roles: ["GovernanceAdmin"]);

    private static TableMetadata Table(string owner, string name = "orders", string source = "src_b") => new()
    {
        Identifier = new TableIdentifier(source, "dbo", name),
        Table = new Table { SourceName = source, SchemaName = "dbo", TableName = name, DataSourceType = DataSourceType.Sql, IsActive = true },
        OwnerTenantId = owner,
        Columns = [],
        PrimaryKeyColumns = []
    };

    [Theory]
    [InlineData("foreign_domain_is_forbidden")]
    [InlineData("cluster_admin_may_register_any_domain")]
    [InlineData("secret_namespace_is_bound_to_the_tenant")]
    [InlineData("foreign_tenant_secret_ref_is_rejected")]
    [InlineData("existing_secret_is_never_overwritten")]
    [InlineData("throwing_provider_denies_the_write")]
    [InlineData("mcp_registration_is_tenant_bound")]
    [InlineData("mcp_registration_refuses_existing_secret")]
    [InlineData("ingestion_refuses_existing_secret")]
    public async Task Ghsa_v5c2_cc86_regression(string bullet)
    {
        var secrets = bullet == "throwing_provider_denies_the_write"
            ? new InMemoryKeyVaultSecretProvider(_ => throw new InvalidOperationException("backend unavailable"))
            : new InMemoryKeyVaultSecretProvider();
        var ingestion = Substitute.For<IOpenApiIngestionService>();
        ingestion.IngestOpenApiJsonAsync(default!, default!, default, default, default, default).ReturnsForAnyArgs(
            new OpenApiIngestionResult(true, "T", 1, 1, ["a"], []));
        var request = new DatasourceRegistrationRequest("Crane", "telemetry", SpecContent: Spec,
            Auth: new DatasourceAuthDto("apiKey", Name: "X-K", Value: "legit-secret"));

        switch (bullet)
        {
            case "foreign_domain_is_forbidden":
                var denied = new StubCatalogDiscoveryService();
                var result = await CatalogApiEndpoints.RegisterDatasourceHandler(request, denied, Http("tenant-a", "GovernanceAdmin"), CancellationToken.None);
                result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(403);
                denied.RegisterCalls.ShouldBeEmpty();
                break;
            case "cluster_admin_may_register_any_domain":
                var allowed = new StubCatalogDiscoveryService();
                await CatalogApiEndpoints.RegisterDatasourceHandler(request, allowed, Http("tenant-a", "ClusterAdmin"), CancellationToken.None);
                allowed.RegisterCalls.Count.ShouldBe(1);
                break;
            case "secret_namespace_is_bound_to_the_tenant":
                (await Discovery(secrets, ingestion).RegisterDatasourceAsync(request, Ctx())).Success.ShouldBeTrue();
                secrets.WrittenRefs.ShouldContain("datasource:telemetry:tenant-a:crane:secret");
                break;
            case "foreign_tenant_secret_ref_is_rejected":
                var foreign = request with { Auth = new DatasourceAuthDto("apiKey", Name: "X-K", SecretRef: "datasource:telemetry:tenant-b:crane:secret") };
                await Should.ThrowAsync<ArgumentException>(() => Discovery(secrets, ingestion).RegisterDatasourceAsync(foreign, Ctx()));
                secrets.WrittenRefs.ShouldBeEmpty();
                break;
            case "existing_secret_is_never_overwritten":
                secrets.Add("datasource:telemetry:tenant-a:crane:secret", [9, 9, 9]);
                await Should.ThrowAsync<ArgumentException>(() => Discovery(secrets, ingestion).RegisterDatasourceAsync(request, Ctx()));
                secrets.WrittenRefs.ShouldBeEmpty();
                break;
            case "throwing_provider_denies_the_write":
                await Should.ThrowAsync<ArgumentException>(() => Discovery(secrets, ingestion).RegisterDatasourceAsync(request, Ctx()));
                secrets.WrittenRefs.ShouldBeEmpty();
                break;
            case "mcp_registration_is_tenant_bound":
                var planning = new AccessPlanningService(AuditWriterTestServices.Over(new InMemoryAuditLogRepository()), new InMemoryTableMetadataRepository(),
                    new InMemoryConsentRepository(), new TotpVerificationService(), new InMemoryTotpSecretStore(), secretProvider: secrets);
                var mcp = new AdminRegisterDatasourceRequest("crane", "telemetry", Auth: new DatasourceAuthDto("apiKey", Secret: "s"));
                await Should.ThrowAsync<UnauthorizedAccessException>(() => planning.RegisterDatasourceAsync(mcp, "admin", "tenant-a"));
                var registered = await planning.RegisterDatasourceAsync(mcp, "admin", "tenant-a", isClusterAdmin: true);
                registered.SecretRef.ShouldBe("datasource:telemetry:tenant-a:crane:auth_secret");
                break;
            case "mcp_registration_refuses_existing_secret":
                secrets.Add("datasource:telemetry:tenant-a:crane:auth_secret", [9]);
                var existing = new AccessPlanningService(AuditWriterTestServices.Over(new InMemoryAuditLogRepository()), new InMemoryTableMetadataRepository(),
                    new InMemoryConsentRepository(), new TotpVerificationService(), new InMemoryTotpSecretStore(), secretProvider: secrets);
                await Should.ThrowAsync<ArgumentException>(() => existing.RegisterDatasourceAsync(
                    new AdminRegisterDatasourceRequest("crane", "telemetry", Auth: new DatasourceAuthDto("apiKey", Secret: "s")), "admin", "tenant-a", isClusterAdmin: true));
                secrets.WrittenRefs.ShouldBeEmpty();
                break;
            case "ingestion_refuses_existing_secret":
                var existingSecrets = new InMemoryKeyVaultSecretProvider([9]);
                var direct = new OpenApiIngestionService(new InMemoryTableMetadataRepository(), NullLogger<OpenApiIngestionService>.Instance, secretProvider: existingSecrets);
                foreach (var (type, scheme) in new[] { ("apiKey", (string?)null), ("http", "bearer"), ("oauth2", null) })
                {
                    var auth = new DatasourceAuthDto(type, Scheme: scheme, Value: "v", ClientSecret: "v", SecretRef: "datasource:telemetry:tenant-a:crane:secret");
                    await Should.ThrowAsync<ArgumentException>(() => direct.IngestOpenApiJsonAsync(Spec, "telemetry", defaultBaseUrl: null, auth: auth, dryRun: false));
                }

                existingSecrets.WrittenRefs.ShouldBeEmpty();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bullet), bullet, "Unknown regression case.");
        }
    }

    [Theory]
    [InlineData("datasource_test_is_confined_to_the_caller_tenant")]
    [InlineData("tenant_confined_caller_skips_the_global_connections_preflight")]
    [InlineData("state_change_on_foreign_tenant_table_is_refused")]
    [InlineData("state_change_on_missing_table_is_not_found_and_unaudited")]
    public async Task Ghsa_9229_wvm9_regression(string bullet)
    {
        var repo = new InMemoryTableMetadataRepository();
        if (bullet.StartsWith("datasource_test", StringComparison.Ordinal))
        {
            // The pre-flight case has no catalog table at all: only the operator-configured connection could match, and a confined caller never reaches it.
            if (!bullet.Contains("preflight", StringComparison.Ordinal))
            {
                repo.Add(Table("tenant-b"));
            }

            var factory = new SpySqlConnectionFactory();
            var gateway = new GatewayOptions { DataSources = new SqlDataSourceOptions() };
            gateway.DataSources.Connections["src_b"] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=h;Database=d;" };
            var testing = new DatasourceTestingService(
                repo, UnusedHttpFactory, AuditWriterTestServices.Over(new InMemoryAuditLogRepository()), null, null, null,
                factory, Options.Create(gateway), new SqlSourceDescriber());

            await Should.ThrowAsync<KeyNotFoundException>(() =>
                testing.TestDatasourceAsync("src_b", new DatasourceTestRequest(), Http("tenant-a").User, TenantA, CancellationToken.None));
            factory.CreateCalls.ShouldBeEmpty();
            return;
        }

        var audit = new InMemoryAuditLogRepository();
        var service = new AccessPlanningService(AuditWriterTestServices.Over(audit), repo, new InMemoryConsentRepository(),
            new TotpVerificationService(), new InMemoryTotpSecretStore());
        if (bullet.Contains("foreign", StringComparison.Ordinal))
        {
            repo.Add(Table("tenant-b"));
        }

        var state = await service.SetDatasetStateAsync(
            new AdminSetDatasetStateRequest("src_b.dbo.orders", "quarantined", "x"), "admin", "tenant-a", TenantA, CancellationToken.None);

        state.Success.ShouldBeFalse();
        repo.UpsertCalls.ShouldBe(0);
        audit.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("other_tenant_rule_never_applies")]
    [InlineData("strictest_rule_wins_in_any_order")]
    [InlineData("rule_change_invalidates_the_cache")]
    public async Task Ghsa_7pjw_hvff_regression(string bullet)
    {
        static TableMetadata WithRule(string owner, string name, string ruleType) => Table(owner, name) with
        {
            Columns = [new TableColumn { ColumnName = "pin" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["pin"] = new MaskingRule { RuleType = ruleType } }
        };

        var repo = new MutableTableRepo();
        var catalog = new CatalogFederatedMaskingCatalog(repo, Options.Create(new GatewayOptions()));

        void Returns(params TableMetadata[] tables) =>
            repo.Tables = tables;

        if (bullet.StartsWith("rule_change", StringComparison.Ordinal))
        {
            Returns(WithRule("tenant-a", "t1", "MASK_EMAIL"));
            (await catalog.GetClassificationsAsync(TenantA))["pin"].RuleType.ShouldBe("MASK_EMAIL");
            Returns(WithRule("tenant-a", "t1", "NULLIFY"));
            catalog.Invalidate(TenantA);
            (await catalog.GetClassificationsAsync(TenantA))["pin"].RuleType.ShouldBe("NULLIFY");
            return;
        }

        var other = bullet.StartsWith("other", StringComparison.Ordinal) ? "tenant-b" : "tenant-a";
        var own = WithRule("tenant-a", "t1", other == "tenant-b" ? "REDACT" : "NULLIFY");
        var second = WithRule(other, "t2", "MASK_EMAIL");
        foreach (var order in new[] { new[] { own, second }, new[] { second, own } })
        {
            Returns(order);
            catalog.Invalidate();
            (await catalog.GetClassificationsAsync(TenantA))["pin"].RuleType.ShouldBe(other == "tenant-b" ? "REDACT" : "NULLIFY");
        }
    }

    [Theory]
    [InlineData("audit_reports_actual_masking_path")]
    [InlineData("non_root_deny_column_never_staged_wildcard")]
    [InlineData("non_root_deny_column_never_staged_explicit")]
    [InlineData("name_clash_root_clear_member_masked_stays_masked")]
    [InlineData("member_mask_applied_per_member")]
    public async Task Ghsa_7ccv_5qwm_regression(string bullet)
    {
        if (bullet != "audit_reports_actual_masking_path")
        {
            await (bullet switch
            {
                "non_root_deny_column_never_staged_wildcard" => FederatedSubtreeColumnDecisionTests.AssertDenyColumnNeverStagedAsync(wildcard: true),
                "non_root_deny_column_never_staged_explicit" => FederatedSubtreeColumnDecisionTests.AssertDenyColumnNeverStagedAsync(wildcard: false),
                "name_clash_root_clear_member_masked_stays_masked" => FederatedSubtreeColumnDecisionTests.AssertMemberMaskAppliedPerMemberAsync(nameClash: true),
                "member_mask_applied_per_member" => FederatedSubtreeColumnDecisionTests.AssertMemberMaskAppliedPerMemberAsync(nameClash: false),
                _ => throw new ArgumentOutOfRangeException(nameof(bullet))
            });
            return;
        }

        var engine = new FastSqlEngine();
        const string sql = "SELECT o.id, oi.item_name FROM pg.public.orders o JOIN pg.public.order_items oi ON o.id = oi.order_id JOIN ms.dbo.shipments s ON o.id = s.id";
        var metadata = engine.Analyze(sql.AsMemory());

        TableMetadata Meta(string domain, string schema, string name, params string[] cols) => new()
        {
            Identifier = new TableIdentifier(domain, schema, name),
            Table = new Table { TableName = name, SchemaName = schema, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql, SourceName = domain, IsActive = true },
            Columns = cols.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList()
        };

        var metas = new[] { Meta("pg", "public", "orders", "id"), Meta("pg", "public", "order_items", "id", "order_id", "item_name"), Meta("ms", "dbo", "shipments", "id") };
        var tables = metas.Select((m, i) => new ResolvedSourceTable(metadata.ReferencedTables[i], m.Identifier, m, m.Identifier.Domain)).ToList();
        var decisions = tables.ToDictionary(t => t.Metadata.Identifier,
            t => TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-REG"), new Claim("tenant_id", "tenant-a")], "TestAuth"));
        var plan = new CrossSourcePlanner(engine).Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true },
            Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        var connector = new StubAutherisConnector(
            "pg",
            rows: [new Dictionary<string, object?> { ["id"] = 1, ["item_name"] = "Widget" }],
            getSplits: (_, _, _) => Task.FromResult<IReadOnlyList<ConnectorSplit>>([ConnectorSplit.Default("s")]));
        var registry = new InMemoryConnectorRegistry();
        registry.RegisterConnector("pg", connector);
        var audit = new InMemoryAuditLogRepository();

        var staging = new FederatedStagingService(registry, StubColumnMaskingProvider.Null, audit,
            Options.Create(new GatewayOptions()), NullLogger<FederatedStagingService>.Instance);
        await staging.StageAsync([plan.StagingRequests.Single(r => r.Subtree != null)], user, TenantA,
            new FederationBudget(5, 1000, 5000, 32 * 1024 * 1024, 128 * 1024 * 1024, 30, 4));

        var members = audit.Entries.Where(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead).ToList();
        members.ShouldNotBeEmpty();
        members.ShouldAllBe(e => JsonDocument.Parse(e.DetailsJson!).RootElement.GetProperty("inDbMaskingExecuted").GetBoolean() == false);
    }

    [Theory]
    [InlineData("definition_connection_gated")]
    [InlineData("definition_data_source_gated")]
    [InlineData("table_source_gated")]
    [InlineData("empty_table_source_still_gated")]
    [InlineData("denied_source_never_reaches_invoker")]
    [InlineData("subscription_without_registered_gate_denied")]
    [InlineData("all_tables_subscription_admission")]
    [InlineData("iceberg_list_hides_denied_sources")]
    [InlineData("webhook_drop_is_recorded")]
    public async Task Ghsa_92vp_pp52_regression(string bullet)
    {
        switch (bullet)
        {
            case "definition_connection_gated":
                await Procedures.ProcedureTransportGateTests.AssertSourceDeniedBeforeInvokeAsync("definition_connection");
                break;
            case "definition_data_source_gated":
                await Procedures.ProcedureTransportGateTests.AssertSourceDeniedBeforeInvokeAsync("definition_data_source");
                break;
            case "table_source_gated":
                await Procedures.ProcedureTransportGateTests.AssertSourceDeniedBeforeInvokeAsync("table_source");
                break;
            case "empty_table_source_still_gated":
                await Procedures.ProcedureTransportGateTests.AssertSourceDeniedBeforeInvokeAsync("empty_table_source_uses_domain");
                await Procedures.ProcedureTransportGateTests.AssertEmptySourceNameStillGatedAsync();
                break;
            case "denied_source_never_reaches_invoker":
                // Every denial case above asserts the invoker and the row-scope resolver were not called.
                await Procedures.ProcedureTransportGateTests.AssertSourceDeniedBeforeInvokeAsync("table_source");
                break;
            case "subscription_without_registered_gate_denied":
                await Autheris.Tests.Unit.GraphQL.CdcSubscriptionGovernorTransportTests.AssertNoGateDeniesTableSubscriptionAsync();
                break;
            case "all_tables_subscription_admission":
                await Autheris.Tests.Unit.GraphQL.CdcSubscriptionGovernorTransportTests.AssertAllTablesAdmissionAsync();
                break;
            case "iceberg_list_hides_denied_sources":
                await Autheris.Tests.Unit.Federation.IcebergListTransportGateTests.AssertListHidesDeniedSourcesAsync();
                break;
            case "webhook_drop_is_recorded":
                await Autheris.Tests.Unit.Streaming.CdcWebhookDeliveryServiceTests.AssertTransportDropIsRecordedAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bullet));
        }
    }

    private sealed class MutableTableRepo : ITableMetadataRepository
    {
        public IReadOnlyList<TableMetadata> Tables { get; set; } = [];
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(Tables.FirstOrDefault(t => t.Identifier == table));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
            => Task.FromResult(Tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Ghsa_xwpx_93pp_regression()
    {
        var httpIdentity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-HTTP"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-1-2-3-400", ClaimValueTypes.String, Autheris.Domain.Security.IdentityClaimIssuers.Pipeline),
            new Claim("tenant_id", "tenant-a")
        ], "Bearer");
        var httpUser = new ClaimsPrincipal(httpIdentity);

        var mcpAppPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-MCP-APP"),
            new Claim("idtyp", "app"),
            new Claim("tenant_id", "tenant-a")
        ], "McpAuth"));

        var aggregation = Substitute.For<Autheris.Application.Analytics.IGovernedAggregationService>();
        aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Autheris.Application.Analytics.AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Result([], 2));

        var metrics = Substitute.For<Autheris.Application.Analytics.ISemanticMetricQueryService>();
        metrics.QueryMetricsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Autheris.Application.Analytics.MetricQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new Autheris.Application.Analytics.MetricQueryResult(
                ["revenue"], "v1", [],
                new Autheris.Application.Analytics.AggregationGroupRow(new Dictionary<string, object?>(), 1, new Dictionary<string, object?>()),
                new Autheris.Application.Analytics.AggregationMetadata(5, Autheris.Application.Analytics.SmallGroupStrategy.Suppress, false, 0, 0, false, false),
                new Dictionary<string, Autheris.Application.Catalog.Semantic.MetricDefinitionSummary>()));

        var approvalRepo = Substitute.For<IConsentApprovalRepository>();
        approvalRepo.GetRequestsByRequesterSidAsync(Arg.Any<Sid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConsentRequest>>([]));

        var catalogClient = Substitute.For<IDataCatalogClient>();
        var catalogSync = Substitute.For<Autheris.Application.Catalog.Interfaces.IDataCatalogSyncService>();

        var table = Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.DefaultTable();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.StubAllTables([table]);
        var relationRepo = Substitute.For<ITableRelationRepository>();
        relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);

        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext { User = httpUser };
        httpContext.Items["TenantId"] = new TenantId("tenant-a");

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(metadataRepo);
        services.AddSingleton(relationRepo);
        services.AddSingleton(Substitute.For<Autheris.Application.Sql.Tree.IGovernedTreeQueryService>());
        services.AddSingleton(aggregation);
        services.AddSingleton(metrics);
        services.AddSingleton(approvalRepo);
        services.AddSingleton(catalogClient);
        services.AddSingleton(catalogSync);
        services.AddSingleton(accessor);
        services.AddSingleton(Options.Create(new GatewayOptions()));
        services.AddSingleton(Options.Create(new AnalyticsOptions()));

        var sp = services.BuildServiceProvider();
        httpContext.RequestServices = sp;
        accessor.HttpContext.Returns(httpContext);

        var executor = await HotChocolate.Execution.RequestExecutorBuilderExtensions.BuildRequestExecutorAsync(
            GraphQLRequestExecutorBuilderExtensions.AddGraphQLServer(services)
                .AddQueryType<Autheris.GraphQL.Types.Query>()
                .AddMutationType<Autheris.GraphQL.Types.Mutation>()
                .AddTypeModule(_ => new Autheris.GraphQL.Catalog.CatalogGraphQlTypeModule(metadataRepo, relationRepo, null, null, null, true))
                .UseRequest<Autheris.GraphQL.Catalog.CatalogOperationCleanupMiddleware>()
                .UseDefaultPipeline());

        // 1. Aggregate resolver uses GlobalState ClaimsPrincipal (app-only, zero groups), not HttpContext.User.
        var aggRes = await executor.ExecuteAsync(HotChocolate.Execution.OperationRequestBuilder.New()
            .SetDocument("{ sales_dbo_orders_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count } }")
            .AddGlobalState(nameof(ClaimsPrincipal), mcpAppPrincipal)
            .Build());
        Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Errors(aggRes).ShouldBeEmpty();
        await aggregation.Received(1).ExecuteAggregationAsync(
            Arg.Is<ClaimsPrincipal>(p => p.GetUserSid() == new Sid("S-1-5-21-MCP-APP") && p.GetGroupSids().Count == 0),
            Arg.Any<Autheris.Application.Analytics.AggregationQuery>(),
            Arg.Any<CancellationToken>());

        // 2. Metric resolver uses GlobalState ClaimsPrincipal, not HttpContext.User.
        var metRes = await executor.ExecuteAsync(HotChocolate.Execution.OperationRequestBuilder.New()
            .SetDocument("{ queryMetrics(metrics: [\"revenue\"]) { count } }")
            .AddGlobalState(nameof(ClaimsPrincipal), mcpAppPrincipal)
            .Build());
        Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Errors(metRes).ShouldBeEmpty();
        await metrics.Received(1).QueryMetricsAsync(
            Arg.Is<ClaimsPrincipal>(p => p.GetUserSid() == new Sid("S-1-5-21-MCP-APP") && p.GetGroupSids().Count == 0),
            Arg.Any<Autheris.Application.Analytics.MetricQueryRequest>(),
            Arg.Any<CancellationToken>());

        // 3. Query.* resolver uses GlobalState ClaimsPrincipal SID rather than HTTP caller's SID.
        var reqRes = await executor.ExecuteAsync(HotChocolate.Execution.OperationRequestBuilder.New()
            .SetDocument("{ myAccessRequests { requesterSid } }")
            .AddGlobalState(nameof(ClaimsPrincipal), mcpAppPrincipal)
            .Build());
        Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Errors(reqRes).ShouldBeEmpty();
        await approvalRepo.Received(1).GetRequestsByRequesterSidAsync(new Sid("S-1-5-21-MCP-APP"), null, Arg.Any<CancellationToken>());
        await approvalRepo.DidNotReceive().GetRequestsByRequesterSidAsync(new Sid("S-1-5-21-HTTP"), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        // 4. Mutation.* resolver rejects non-admin GlobalState principal despite HttpContext.User holding GovernanceAdmin.
        var mutRes = await executor.ExecuteAsync(HotChocolate.Execution.OperationRequestBuilder.New()
            .SetDocument("mutation { syncDataCatalog { success } }")
            .AddGlobalState(nameof(ClaimsPrincipal), mcpAppPrincipal)
            .Build());
        Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Errors(mutRes).ShouldContain(e => e.Code == "FORBIDDEN");
        await catalogSync.DidNotReceiveWithAnyArgs().SyncCatalogAsync(default, default, default);
    }
}
