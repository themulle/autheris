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
    public async Task Ghsa_5fqm_mpc4_regression()
    {
        var ordersId = new TableIdentifier("sales", "dbo", "orders");
        var orders = new TableMetadata
        {
            Identifier = ordersId,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([orders]));
        Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeSingleReadsFromCatalog(tables);

        var consents = Substitute.For<IConsentRepository>();
        consents.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));

        var created = new List<ConsentRequest>();
        var approvals = Substitute.For<IConsentApprovalRepository>();
        approvals.CreateConsentRequestAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var r = ci.Arg<ConsentRequest>();
                created.Add(r);
                return Task.FromResult(r);
            });

        var extSystemId = Guid.NewGuid();
        var router = Substitute.For<Autheris.Application.ConsentRequests.Routing.IConsentWorkflowRouter>();
        router.ResolveForTableAsync(TenantA, ordersId, Arg.Any<CancellationToken>())
            .Returns(new Autheris.Application.ConsentRequests.Routing.ConsentWorkflowRouteResolution(
                ConsentWorkflowMode.External, extSystemId, MaxDurationDays: 30, RequestTimeoutDays: 14, ExpiryNoticeLeadHours: 24, RequireResolvableApprover: false, WorkflowRouteScopeKind.DataSource));

        var fixedNow = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(fixedNow);
        var limiter = Substitute.For<IRateLimiterService>();
        limiter.CheckCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(new CostQuotaResult(true, 10, 0));

        var svc = new Autheris.Application.Governance.Analysis.BundledConsentRequestService(
            tables,
            consents,
            new InMemoryAuditLogRepository(),
            approvals,
            gatewayOptions: Options.Create(new GatewayOptions()),
            workflowRouter: router,
            rateLimiter: limiter,
            timeProvider: clock);

        var personUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
            new Claim(ClaimTypes.Email, "user@example.com"),
            new Claim("tenant_id", "tenant-a")
        ], "Bearer"));

        // 1. External workflow routing and 30-day route duration cap are enforced on default and explicit durations.
        var clamped = await svc.SubmitBundledRequestAsync(
            personUser,
            new Autheris.Application.Governance.Analysis.BundledConsentRequest(
                [new Autheris.Application.Governance.Analysis.RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need",
                ConsumerReference: "PR-1"));
        clamped.Tickets.ShouldHaveSingleItem().Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
        created.ShouldHaveSingleItem();
        created[0].WorkflowMode.ShouldBe(ConsentWorkflowMode.External);
        created[0].ExternalSystemId.ShouldBe(extSystemId);
        created[0].Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
        created[0].RequestedValidTo.ShouldBe(fixedNow.AddDays(30));
        created[0].RequesterIdentifiers.ShouldNotBeNull();
        created[0].RequesterIdentifiers.ShouldContain("S-1-5-21-USER");
        created[0].RequesterIdentifiers.ShouldContain("user@example.com");

        var overCapEx = await Should.ThrowAsync<Autheris.Application.ConsentRequests.ConsentRequestException>(() => svc.SubmitBundledRequestAsync(
            personUser,
            new Autheris.Application.Governance.Analysis.BundledConsentRequest(
                [new Autheris.Application.Governance.Analysis.RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need",
                RequestedUntilUtc: fixedNow.AddDays(60))));
        overCapEx.Code.ShouldBe("VALIDITY_EXCEEDS_MAXIMUM");

        // 2. App-only tokens, short justifications, disabled self-service, and rate-limit exhaustion are rejected before creation.
        var appOnly = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-APP"),
            new Claim("idtyp", "app"),
            new Claim("tenant_id", "tenant-a")
        ], "Bearer"));
        (await Should.ThrowAsync<Autheris.Application.ConsentRequests.ConsentRequestException>(() => svc.SubmitBundledRequestAsync(
            appOnly,
            new Autheris.Application.Governance.Analysis.BundledConsentRequest(
                [new Autheris.Application.Governance.Analysis.RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need"))))
            .Code.ShouldBe("FORBIDDEN");

        (await Should.ThrowAsync<Autheris.Application.ConsentRequests.ConsentRequestException>(() => svc.SubmitBundledRequestAsync(
            personUser,
            new Autheris.Application.Governance.Analysis.BundledConsentRequest(
                [new Autheris.Application.Governance.Analysis.RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "ab"))))
            .Code.ShouldBe("VALIDATION_ERROR");

        var disabledSvc = new Autheris.Application.Governance.Analysis.BundledConsentRequestService(
            tables,
            consents,
            new InMemoryAuditLogRepository(),
            approvals,
            gatewayOptions: Options.Create(new GatewayOptions
            {
                Authentication = new AuthenticationOptions { EntraId = new EntraIdAuthOptions { SelfServiceEnabled = false } }
            }));
        (await Should.ThrowAsync<Autheris.Application.ConsentRequests.ConsentRequestException>(() => disabledSvc.SubmitBundledRequestAsync(
            personUser,
            new Autheris.Application.Governance.Analysis.BundledConsentRequest(
                [new Autheris.Application.Governance.Analysis.RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need"))))
            .Code.ShouldBe("FORBIDDEN");

        limiter.CheckCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(new CostQuotaResult(false, 0, 42));
        var rlEx = await Should.ThrowAsync<Autheris.Application.ConsentRequests.ConsentRequestException>(() => svc.SubmitBundledRequestAsync(
            personUser,
            new Autheris.Application.Governance.Analysis.BundledConsentRequest(
                [new Autheris.Application.Governance.Analysis.RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need")));
        rlEx.Code.ShouldBe("RATE_LIMIT_EXCEEDED");
        rlEx.RetryAfterSeconds.ShouldBe(42);
    }
}
