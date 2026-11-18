namespace Autheris.Tests.Unit.Governance.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.External;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Federation.Services;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Governance.Paging;
using Autheris.Application.OData;
using Autheris.Application.OData.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Extensions.Dbt;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogCallerMigrationBatch3Tests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid AliceSid = new("S-1-5-21-100-1");

    [Fact]
    public async Task DynamicOpenApi_ODataHandler_And_ODataEndpoint_UsePagedQueries_ExcludeInactive_NeverGetAllTablesAsync()
    {
        var activeInvoices = MakeTable("tenant-a", "dbo", "invoices", isActive: true, sensitiveCol: false);
        var activeLeads = MakeTable("tenant-a", "dbo", "leads", isActive: true, sensitiveCol: false);
        var inactiveArchived = MakeTable("tenant-a", "dbo", "archived", isActive: false, sensitiveCol: false);
        var repo = new RecordingCatalogRepository([activeInvoices, activeLeads, inactiveArchived], pageSize: 1);

        var openApi = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var json = await openApi.GenerateOpenApiJsonAsync(null);
        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.Count.ShouldBeGreaterThanOrEqualTo(2);
        using (var doc = JsonDocument.Parse(json))
        {
            var paths = doc.RootElement.GetProperty("paths");
            paths.TryGetProperty("/tenant-a/dbo/invoices", out _).ShouldBeTrue();
            paths.TryGetProperty("/tenant-a/dbo/leads", out _).ShouldBeTrue();
            paths.TryGetProperty("/tenant-a/dbo/archived", out _).ShouldBeFalse();
        }

        var exec = new StubGatewayExecutionService();
        var odataHandler = new ODataHandler(repo, exec, NullLogger<ODataHandler>.Instance);
        var tenantUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, AliceSid.Value),
            new Claim("tenant_id", TenantA.Value)
        ], "TestAuth"));
        var csdl = await odataHandler.GetMetadataCsdlAsync(tenantUser);
        csdl.ShouldContain("invoices");
        csdl.ShouldNotContain("archived");

        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            User = tenantUser,
            RequestServices = services.BuildServiceProvider()
        };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost", 8080);
        httpContext.Request.Path = "/odata/v4/tenant-a_dbo_invoices";
        var endpointResult = await ODataEndpoints.HandleFlatEntitySetRequestAsync(
            "tenant-a_dbo_invoices",
            odataHandler,
            repo,
            httpContext);
        endpointResult.ShouldBeOfType<JsonHttpResult<object>>();
        repo.GetAllCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EffectiveAccessService_ExactFallbackAndPattern_And_GrantEffectPreview_NeverGetAllTablesAsync()
    {
        var orders = MakeTable("pg", "sales", "orders", isActive: true, sensitiveCol: false, ownerTenant: TenantA.Value);
        var archived = MakeTable("pg", "sales", "archived", isActive: false, sensitiveCol: false, ownerTenant: TenantA.Value);
        var foreign = MakeTable("pg", "hr", "payroll", isActive: true, sensitiveCol: false, ownerTenant: "tenant-b");
        var repo = new RecordingCatalogRepository([orders, archived, foreign]) { ReturnNullOnSingleLookup = true };

        var consents = new StubConsentRepo([]);
        var owners = new StubOwnershipRepo();
        var visibility = new StubVisibilityService([orders, archived, foreign]);
        var audit = new StubAuditWriter();
        var resolution = new ConsentResolutionService();
        var pdp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            resolution,
            consentRepository: consents,
            auditWriter: audit,
            ownershipRepository: owners);
        var subjects = new StubSubjectResolver();
        var caller = BuildContext(AliceSid, TenantA);
        subjects.Register(AliceSid.Value, caller);

        var effectiveAccess = new EffectiveAccessService(pdp, repo, visibility, owners, consents, subjects, audit);
        var exactResult = await effectiveAccess.GetEffectiveAccessAsync(
            caller,
            new EffectiveAccessReportQuery(AliceSid.Value, "pg.sales.orders"));
        exactResult.Outcome.ShouldBe(EffectiveAccessOutcome.Ok);
        exactResult.Report!.Objects.Count.ShouldBe(1);
        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.ShouldContain(q => q.DataSource == "pg" && q.Schema == "sales" && q.NamePrefix == "orders");

        var patternResult = await effectiveAccess.GetEffectiveAccessAsync(
            caller,
            new EffectiveAccessReportQuery(AliceSid.Value, "pg.sales.*"));
        patternResult.Outcome.ShouldBe(EffectiveAccessOutcome.Ok);
        patternResult.Report!.Objects.Count.ShouldBe(1);

        var preview = new GrantEffectPreviewService(repo, consents, audit);
        var previewResult = await preview.PreviewAsync(
            caller,
            [new GrantPreviewRequestedItem("pg.sales.*")]);
        previewResult.MatchedObjectCount.ShouldBe(1);
        repo.GetAllCalls.ShouldBe(0);
    }

    [Fact]
    public async Task FederatedMasking_DbtExposure_ExternalCatalogReader_And_Indexer_NeverGetAllTablesAsync()
    {
        var active = MakeTable("pg", "sales", "customers", isActive: true, sensitiveCol: true, ownerTenant: TenantA.Value);
        var inactive = MakeTable("pg", "sales", "old_customers", isActive: false, sensitiveCol: true, ownerTenant: TenantA.Value);
        var repo = new RecordingCatalogRepository([active, inactive], pageSize: 1);

        var maskingCatalog = new CatalogFederatedMaskingCatalog(
            repo,
            Options.Create(new GatewayOptions()));
        var classifications = await maskingCatalog.GetClassificationsAsync(TenantA);
        classifications.ShouldContainKey("secretcolactive");
        classifications.ShouldNotContainKey("secretcololdold_customers");

        var publisher = new DbtExposurePublisher(repo, NullLogger<DbtExposurePublisher>.Instance);
        var yaml = await publisher.GenerateExposuresYamlAsync();
        yaml.ShouldContain("customers");
        yaml.ShouldNotContain("old_customers");

        var consents = new InMemoryExternalConsentRepository();
        var owners = new PerUserOwnershipRepository();
        var changeStore = new InMemoryCatalogChangeStore();
        changeStore.ExtraTenantIds.Add(TenantA.Value);
        var gatewayOptions = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
        var indexer = new CatalogChangeIndexerHostedService(
            repo,
            consents,
            owners,
            changeStore,
            gatewayOptions);
        await indexer.IndexOnceAsync();
        await indexer.IndexTenantAsync(TenantA, preloadedTables: null);

        var reader = new ExternalCatalogReader(
            repo,
            new StubDiscoveryService(),
            consents,
            owners,
            new InMemoryExternalWorkflowRepository(),
            changeStore,
            gatewayOptions);
        var appUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, AliceSid.Value),
            new Claim("tenant_id", TenantA.Value)
        ], "TestAuth"));
        var listResult = await reader.ListObjectsAsync(appUser, TenantA, new ExternalCatalogListQuery());
        listResult.IsSuccess.ShouldBeTrue();

        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.Count.ShouldBeGreaterThan(0);
    }

    private static TableMetadata MakeTable(
        string domain,
        string schema,
        string tableName,
        bool isActive,
        bool sensitiveCol,
        string? ownerTenant = null) =>
        new()
        {
            Identifier = new TableIdentifier(domain, schema, tableName),
            OwnerTenantId = ownerTenant,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = domain,
                SchemaName = schema,
                TableName = tableName,
                SourceType = "postgresql",
                IsActive = isActive
            },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = sensitiveCol ? $"secret_col_{(isActive ? "active" : "old_" + tableName)}" : "note", DataType = "varchar" }
            ],
            ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(
                sensitiveCol ? new[] { $"secret_col_{(isActive ? "active" : "old_" + tableName)}" } : Array.Empty<string>())
        };

    private static SecurityPrincipalContext BuildContext(Sid userSid, TenantId tenant) =>
        new()
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, userSid.Value),
                new Claim("tid", tenant.Value)
            ], "test")),
            UserSid = userSid,
            TenantId = tenant,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "test"
        };

    private sealed class RecordingCatalogRepository(IReadOnlyList<TableMetadata> tables, int pageSize = 100) : ITableMetadataRepository
    {
        public int GetAllCalls { get; private set; }
        public List<CatalogTableQuery> QueryCalls { get; } = [];
        public bool ReturnNullOnSingleLookup { get; init; }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(ReturnNullOnSingleLookup ? null : tables.FirstOrDefault(t => t.Identifier == table));

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            return Task.FromResult(tables);
        }

        public Task<CatalogTablePage> QueryTablesAsync(CatalogTableQuery query, CancellationToken ct = default)
        {
            QueryCalls.Add(query);
            query.EnsureValid(out var domain, out var schema, out var namePrefix, out var after);
            var limit = Math.Min(query.EffectiveLimit, pageSize);
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
            var hasMore = matched.Count > limit;
            var items = hasMore ? matched.Take(limit).ToList() : matched;
            var nextAfter = hasMore ? CatalogTableCursor.Encode(items[^1].Identifier) : null;
            return Task.FromResult(new CatalogTablePage(items, nextAfter));
        }

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubGatewayExecutionService : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal, TableIdentifier table, int? first = null, int? after = null, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>((
                [new Dictionary<string, object?> { ["id"] = 1 }],
                TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));

        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal, TableIdentifier table, int? first, int? after,
            IReadOnlyDictionary<string, object?>? queryArguments, IReadOnlyList<string>? requestedFields = null,
            IReadOnlyDictionary<string, string[]>? requestHeaders = null, CancellationToken ct = default) =>
            ExecuteTableQueryAsync(principal, table, first, after, ct);

        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
    }

    private sealed class StubSubjectResolver : IExplainSubjectResolver
    {
        private readonly Dictionary<string, SecurityPrincipalContext> _map = new(StringComparer.Ordinal);
        public void Register(string sid, SecurityPrincipalContext ctx) => _map[sid] = ctx;
        public ValueTask<SecurityPrincipalContext?> ResolveAsync(string? sid, string? upn, TenantId tenant, CancellationToken ct) =>
            ValueTask.FromResult(sid is not null && _map.TryGetValue(sid, out var found) && found.TenantId == tenant ? found : null);
    }

    private sealed class StubAuditWriter : IAuditWriter
    {
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubVisibilityService(IReadOnlyList<TableMetadata> tables) : ICatalogVisibilityService
    {
        public ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, SchemaContractDefinition? contract = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new CatalogVisibleSet(tables.Where(t => t.Table.IsActive).ToList(), CallerScope.Global, isAdminView: true));

        public ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default)
        {
            var found = tables.FirstOrDefault(t => t.Identifier == table && t.Table.IsActive);
            return ValueTask.FromResult<CatalogVisibleRow?>(found is null ? null : new CatalogVisibleRow(found, CatalogOutputShape.Full));
        }

        public ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlySet<TableIdentifier>>(tables.Where(t => t.Table.IsActive && candidates.Contains(t.Identifier)).Select(t => t.Identifier).ToHashSet());
    }

    private sealed class StubOwnershipRepo : IDataOwnershipRepository
    {
        public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DataOwner>>([]);
        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class StubConsentRepo(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) =>
            Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) =>
            Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult(consents);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubDiscoveryService : IDiscoveryService
    {
        public ValueTask<DiscoveryPage> ListAsync(DiscoveryQuery query, CatalogCaller caller, CancellationToken ct = default) =>
            ValueTask.FromResult(new DiscoveryPage([], 0, false));
        public ValueTask<DiscoveryObject?> GetAsync(TableIdentifier id, CatalogCaller caller, CancellationToken ct = default) =>
            ValueTask.FromResult<DiscoveryObject?>(null);
        public ValueTask<IReadOnlyList<DiscoveryObject>> SearchAsync(string text, DiscoverySearchOptions options, CatalogCaller caller, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<DiscoveryObject>>([]);
    }
}
