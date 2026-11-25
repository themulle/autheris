namespace Autheris.Tests.Unit.Governance.Catalog;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration.Validation;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Infrastructure.Cache;
using Autheris.Application.DataSources;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Mcp.Tools;
using Autheris.Application.Policy;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Tests.Unit.Sql;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Health;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.GraphQL;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogCallerMigrationBatch2Tests
{
    private sealed class RecordingCatalogRepository(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public int GetAllCalls { get; private set; }
        public List<CatalogTableQuery> QueryCalls { get; } = [];
        public bool ReturnNullPage { get; init; }
        public bool RepeatCursor { get; init; }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(tables.FirstOrDefault(t => t.Identifier == table));

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            return Task.FromResult(tables);
        }

        public Task<CatalogTablePage> QueryTablesAsync(CatalogTableQuery query, CancellationToken ct = default)
        {
            QueryCalls.Add(query);
            if (ReturnNullPage)
            {
                return Task.FromResult<CatalogTablePage>(null!);
            }

            query.EnsureValid(out var domain, out var schema, out var namePrefix, out var after);
            var limit = query.EffectiveLimit;
            var matched = tables
                .Where(t => query.IncludeInactive || t.Table.IsActive)
                .Where(t => domain is null || string.Equals(t.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
                .Where(t => schema is null || string.Equals(t.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase))
                .Where(t => namePrefix is null || t.Identifier.TableName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(t => after is null || CatalogTableCursor.Compare(t.Identifier, after.Value) > 0)
                .OrderBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.TableName, StringComparer.OrdinalIgnoreCase)
                .Take(limit + 1)
                .ToList();

            var hasMore = RepeatCursor || matched.Count > limit;
            var items = matched.Count > limit ? matched.Take(limit).ToList() : matched;
            var nextAfter = RepeatCursor
                ? (items.Count > 0 ? CatalogTableCursor.Encode(items[0].Identifier) : query.After ?? CatalogTableCursor.Encode(new TableIdentifier("a", "b", "c")))
                : (matched.Count > limit ? CatalogTableCursor.Encode(items[^1].Identifier) : null);
            return Task.FromResult(new CatalogTablePage(items, nextAfter));
        }

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

#pragma warning disable CA1852
    private class GovernanceRepoDispatchProxy : DispatchProxy
#pragma warning restore CA1852
    {
        private RecordingCatalogRepository _catalog = null!;

        public static IGovernanceRepository Create(RecordingCatalogRepository catalog)
        {
            var proxy = Create<IGovernanceRepository, GovernanceRepoDispatchProxy>();
            ((GovernanceRepoDispatchProxy)(object)proxy)._catalog = catalog;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.DeclaringType == typeof(ITableMetadataRepository)
                ? targetMethod.Invoke(_catalog, args)
                : throw new NotSupportedException($"Unexpected call to {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}.");
    }

    private sealed class StubConsentRepository(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetActiveInternalPatternConsentsAsync(DateTimeOffset atTime, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c => c.IsPatternGrant && c.WorkflowMode == ConsentWorkflowMode.Internal && c.Effect == ConsentEffect.Allow).ToList());
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => Task.FromResult(new ConsentPage([], null));
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubOwnershipRepo : IContainerOwnership
    {
        public Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContainerOwnerEntry>>([new ContainerOwnerEntry { AdSid = new Sid("S-1-5-21-OWNER"), IsActive = true }]);
        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class StubApprovalRepo : IConsentApprovalRepository
    {
        public List<ConsentRequest> Created { get; } = [];
        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) { Created.Add(request); return Task.FromResult(request); }
        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.FromResult<ConsentRequest?>(null);
        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedVersionProvider(CatalogVersion version) : ICatalogVersionProvider
    {
        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => new(version);
        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(version.Formatted);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler());
    }

    private static TableMetadata MakeTable(string domain, string schema, string name, bool active = true, string? ownerTenantId = null, HttpEndpointDescriptor? endpoint = null) => new()
    {
        Identifier = new TableIdentifier(domain, schema, name),
        OwnerTenantId = ownerTenantId ?? TestTenants.Default.Value,
        Table = new Table
        {
            SourceName = domain,
            SchemaName = schema,
            TableName = name,
            DataSourceType = endpoint is null ? DataSourceType.Sql : DataSourceType.HttpDeclarative,
            IsActive = active,
            HttpEndpoint = endpoint
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static ClaimsPrincipal UserWithSid(string sid = "S-1-5-21-TEST") => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.PrimarySid, sid),
        new Claim("tenant_id", TestTenants.Default.Value)
    ], "Bearer"));

    [Fact]
    public async Task GatewayHealthCheckService_Fallback_CallsQueryTablesLimit1_NeverGetAllTablesAsync()
    {
        var repo = new RecordingCatalogRepository([MakeTable("crm", "dbo", "orders"), MakeTable("crm", "dbo", "users")]);
        var service = new GatewayHealthCheckService(
            Options.Create(new GatewayOptions()),
            NullLogger<GatewayHealthCheckService>.Instance,
            governanceRepository: GovernanceRepoDispatchProxy.Create(repo));

        var report = await service.CheckHealthAsync();

        report.Components.Single(c => c.Name == "GovernanceDb").IsHealthy.ShouldBeTrue();
        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.ShouldHaveSingleItem().Limit.ShouldBe(1);
    }

    [Fact]
    public async Task DmlDependencyScan_StopsFetchingOnEarlyMatch_SkipsInactive_AndFailsClosedOnCursorErrors()
    {
        var tables = Enumerable.Range(0, 105)
            .Select(i => MakeTable("crm", "public", $"t_{i:D3}", active: i != 0))
            .ToList();
        var repo = new RecordingCatalogRepository(tables);
        var consentsId = new TableIdentifier("crm", "public", "consents");
        var page1Trigger = tables[50].Identifier;
        var page2Trigger = tables[102].Identifier;

        var scan = new DmlDependencyScan(
            repo,
            (_, id, _) =>
            {
                if (id == page1Trigger)
                {
                    return Task.FromResult(TableAccessDecision.Allowed(
                        id,
                        new Dictionary<string, ColumnAccessLevel>(),
                        null,
                        hasUnconstrainedColumnAllow: true) with
                    {
                        RowPolicy = SecuredTreeHarness.ExistsPolicy(schema: "public")
                    });
                }

                return Task.FromResult(TableAccessDecision.Denied(id, "not allowed"));
            },
            "crm");

        (await scan.IsReadAsync(consentsId, CancellationToken.None)).ShouldBeTrue();
        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.Count.ShouldBe(1);

        var multiPageScan = new DmlDependencyScan(
            repo,
            (_, id, _) =>
            {
                if (id == page2Trigger)
                {
                    return Task.FromResult(TableAccessDecision.Allowed(
                        id,
                        new Dictionary<string, ColumnAccessLevel>(),
                        null,
                        hasUnconstrainedColumnAllow: true) with
                    {
                        RowPolicy = SecuredTreeHarness.ExistsPolicy(schema: "public")
                    });
                }

                return Task.FromResult(TableAccessDecision.Denied(id, "not allowed"));
            },
            "crm");

        (await multiPageScan.IsReadAsync(consentsId, CancellationToken.None)).ShouldBeTrue();
        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.Count.ShouldBe(3);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new DmlDependencyScan(
                new RecordingCatalogRepository(tables) { ReturnNullPage = true },
                (_, id, _) => Task.FromResult(TableAccessDecision.Denied(id, "no")),
                "crm").IsReadAsync(consentsId, CancellationToken.None));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new DmlDependencyScan(
                new RecordingCatalogRepository(tables) { RepeatCursor = true },
                (_, id, _) => Task.FromResult(TableAccessDecision.Denied(id, "no")),
                "crm").IsReadAsync(consentsId, CancellationToken.None));
    }

    [Fact]
    public async Task TrinoMetadataQueryHandler_ScopesShowQueries_AndExcludesInactive_NeverGetAllTablesAsync()
    {
        var active = MakeTable("sales", "dbo", "orders");
        var inactive = MakeTable("sales", "dbo", "retired", active: false);
        var other = MakeTable("hr", "priv", "employees");
        var repo = new RecordingCatalogRepository([active, inactive, other]);
        var consent = new Consent
        {
            TableIdentifier = active.Identifier,
            TenantId = TestTenants.Default,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-TEST"),
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        var consents = new StubConsentRepository([consent]);
        var visibility = TrinoMetadataVisibilityFakes.AllowAll(active, inactive, other);

        var showTables = await TrinoMetadataQueryHandler.TryExecuteAsync("SHOW TABLES FROM sales.dbo", UserWithSid(), TestTenants.Default, repo, consents, null, null, visibility);
        showTables.ShouldNotBeNull();
        showTables!.Rows.Select(r => r["Table"]).ShouldBe(["orders"]);

        var showSchemas = await TrinoMetadataQueryHandler.TryExecuteAsync("SHOW SCHEMAS FROM sales", UserWithSid(), TestTenants.Default, repo, consents, null, null, visibility);
        showSchemas.ShouldNotBeNull();

        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.Count.ShouldBe(2);
        repo.QueryCalls[0].DataSource.ShouldBe("sales");
        repo.QueryCalls[0].Schema.ShouldBe("dbo");
        repo.QueryCalls[1].DataSource.ShouldBe("sales");
    }

    [Fact]
    public async Task BackgroundAndStartupCallers_UseBoundedPagedReads_NeverGetAllTablesAsync()
    {
        var active = MakeTable("pg", "hr", "salaries");
        var inactive = MakeTable("pg", "hr", "archived", active: false);
        var repo = new RecordingCatalogRepository([active, inactive]);
        var grant = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = active.Identifier,
            ObjectPattern = "pg.hr.*",
            WorkflowMode = ConsentWorkflowMode.Internal,
            Effect = ConsentEffect.Allow,
            TenantId = TestTenants.Default,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-TEST"),
            ApprovingOwners = ["S-1-5-21-ADMIN"],
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        var approvals = new StubApprovalRepo();

        var services = new ServiceCollection();
        services.AddSingleton<IConsentRepository>(new StubConsentRepository([grant]));
        services.AddSingleton<ITableMetadataRepository>(repo);
        services.AddSingleton<IContainerOwnership>(new StubOwnershipRepo());
        services.AddSingleton<IConsentApprovalRepository>(approvals);
        services.AddSingleton<IAuditLogRepository>(NullAuditLogRepository.Instance);
        using var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var monitor = new PatternCoverageMonitor(scopeFactory, new FixedVersionProvider(new CatalogVersion(1, 0)), new Autheris.Application.State.InMemoryClusterStateProvider(), NullLogger<PatternCoverageMonitor>.Instance);
        (await monitor.RunOnceAsync(CancellationToken.None)).ShouldBe(1);
        approvals.Created.ShouldHaveSingleItem().TableIdentifier.ShouldBe(active.Identifier);

        var reporterLogger = new CapturingLogger<DataSourceTransportConfigurationReporter>();
        using var transportReporter = new DataSourceTransportConfigurationReporter(
            new StaticOptionsMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions()),
            () => new GatewayOptions(),
            _ => null,
            reporterLogger,
            scopeFactory);
        await transportReporter.StartAsync(CancellationToken.None);
        reporterLogger.Entries.ShouldContain(e => e.Message.Contains("pg", StringComparison.OrdinalIgnoreCase));

        using var deniedReporter = new DeniedTablesStartupReporter(
            repo,
            null,
            null,
            Options.Create(new GatewayOptions()),
            NullLogger<DeniedTablesStartupReporter>.Instance);
        await deniedReporter.StartAsync(CancellationToken.None);
        await deniedReporter.ExecuteTask!;

        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task DatasourceTesting_Visibility_Guardrail_And_QueryAnalysis_NeverCallGetAllTablesAsync()
    {
        var orders = MakeTable("alpha", "dbo", "orders");
        var extra = Enumerable.Range(0, 105).Select(i => MakeTable($"zsrc{i:D3}", "dbo", $"t_{i:D3}")).ToList();
        var foreignTenant = MakeTable("foreign", "dbo", "secrets", ownerTenantId: "other-tenant");
        var repo = new RecordingCatalogRepository([orders, foreignTenant, .. extra]);

        var endpoint = new HttpEndpointDescriptor { Name = "alpha-ep", BaseUrl = "https://example.invalid", PathTemplate = "/ping", AuthMode = HttpAuthMode.None };
        var httpOrders = MakeTable("alpha", "dbo", "orders", endpoint: endpoint);
        var dsRepo = new RecordingCatalogRepository([httpOrders, foreignTenant, .. extra]);
        var dsTesting = new DatasourceTestingService(
            dsRepo,
            new StubHttpClientFactory(),
            AuditWriterTestServices.Over(NullAuditLogRepository.Instance),
            logger: NullLogger<DatasourceTestingService>.Instance);

        var ok = await dsTesting.TestDatasourceAsync("alpha", new DatasourceTestRequest(), UserWithSid(), restrictToTenant: TestTenants.Default);
        ok.ShouldNotBeNull();
        dsRepo.GetAllCalls.ShouldBe(0);
        dsRepo.QueryCalls.Count.ShouldBe(1);

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            dsTesting.TestDatasourceAsync("foreign", new DatasourceTestRequest(), UserWithSid(), restrictToTenant: TestTenants.Default));

        var consent = new Consent
        {
            TableIdentifier = orders.Identifier,
            TenantId = TestTenants.Default,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-TEST"),
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        var consents = new StubConsentRepository([consent]);

        var visibility = new CatalogVisibilityService(repo, consents, Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }));
        var visible = await visibility.GetVisibleAsync(new CatalogCaller(UserWithSid(), TestTenants.Default, CatalogChannel.Rest));
        visible.Tables.Select(t => t.Identifier).ShouldBe([orders.Identifier]);

        var registry = new McpToolRegistry();
        registry.RegisterTool(new McpToolDefinition("get_golden_queries", "Golden", "{}", string.Empty));
        var golden = new GoldenQueryService(Options.Create(new GatewayOptions()), NullLogger<GoldenQueryService>.Instance);
        golden.RegisterGoldenQuery(new GoldenQuery("gq-1", "alpha", "orders", "Orders", "Desc", "SELECT 1"));
        var guardrail = new AiDataGuardrailService(
            registry,
            Options.Create(new GatewayOptions()),
            NullLogger<AiDataGuardrailService>.Instance,
            NullAuditLogRepository.Instance,
            tableMetadataRepository: repo,
            goldenQueryService: golden,
            consentRepository: consents,
            decisionPoint: PdpTestDoubles.AllowAll());
        var session = new McpSessionContext("s1", "sp1", TestTenants.Default.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "S-1-5-21-TEST", [], []);
        var gqRes = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("get_golden_queries", "{}", session.SessionId), session, CancellationToken.None);
        gqRes.IsSuccess.ShouldBeTrue();
        gqRes.ContentJson.ShouldContain("gq-1");

        var analysis = new QueryAnalysisService(repo, consents, NullAuditLogRepository.Instance);
        var analyzed = await analysis.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql("SELECT id FROM alpha.dbo.orders")], UserWithSid(), ct: CancellationToken.None);
        analyzed.ShouldNotBeNull();

        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.Count.ShouldBeGreaterThanOrEqualTo(6);
    }
}
