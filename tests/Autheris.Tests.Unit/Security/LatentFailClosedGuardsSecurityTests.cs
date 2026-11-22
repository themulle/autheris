using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Compliance;
using Autheris.Application.DataSources;
using Autheris.Application.DataSources.Connectors;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Federation.Services;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Products;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Governance.Classification;
using Autheris.Tests.Unit.Products;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class LatentFailClosedGuardsSecurityTests
{
    [Fact]
    public async Task SaveFilterAsync_SqlFilter_WithNullActorRoles_ThrowsUnauthorizedAccessException()
    {
        var service = new VirtualFilterAdministrationService(
            new InMemoryVfRepo(),
            new InMemoryAuditLogRepository());

        var sqlFilter = new VirtualFilter
        {
            TenantId = new TenantId("tenant-a"),
            Name = "restricted_sql",
            Source = "sales",
            Sql = "from dbo.orders o where target.id = o.id"
        };

        var actorWithNullRoles = new VirtualFilterActor(new Sid("S-1-5-21-USER"), IsSync: false, Roles: null);

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            service.SaveFilterAsync(sqlFilter, actorWithNullRoles));
    }

    [Fact]
    public async Task SubgraphContextPropagation_MissingHostEnvironment_FailsClosedOnDefaultOrMissingKey()
    {
        var options = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableZeroTrustContextForwarding = true,
                SignContextHeaders = true,
                SigningKey = null
            }
        });

        var service = new SubgraphContextPropagationService(
            options,
            NullLogger<SubgraphContextPropagationService>.Instance,
            environment: null);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.internal/graphql");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER")], "Test"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            service.ApplySecurityHeadersAsync(request, "products", user, "tenant-a"));
    }

    [Fact]
    public async Task SubgraphContextPropagation_StreamContentBody_HashesWithoutDisposingOrConsumingStream()
    {
        const string payload = "{\"query\":\"{ orders { id } }\"}";
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        using var seekableStream = new System.IO.MemoryStream(payloadBytes);
        using var nonSeekableStream = new NonSeekableMemoryStream(payloadBytes);

        var options = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableZeroTrustContextForwarding = true,
                SignContextHeaders = true,
                SigningKey = "super-secret-signing-key-for-subgraphs-123!"
            }
        });

        var service = new SubgraphContextPropagationService(
            options,
            NullLogger<SubgraphContextPropagationService>.Instance);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER")], "Test"));

        using var req1 = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.internal/graphql")
        {
            Content = new StreamContent(seekableStream)
        };
        await service.ApplySecurityHeadersAsync(req1, "products", user, "tenant-a");

        seekableStream.CanRead.ShouldBeTrue("StreamContent's underlying stream must not be disposed.");
        req1.Headers.GetValues(FederationContextSignature.BodyHashHeader).Single()
            .ShouldBe(FederationContextSignature.HashBody(payloadBytes));
        (await req1.Content!.ReadAsStringAsync()).ShouldBe(payload);

        using var req2 = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.internal/graphql")
        {
            Content = new StreamContent(nonSeekableStream)
        };
        await service.ApplySecurityHeadersAsync(req2, "products", user, "tenant-a");

        req2.Headers.GetValues(FederationContextSignature.BodyHashHeader).Single()
            .ShouldBe(FederationContextSignature.HashBody(payloadBytes));
        (await req2.Content!.ReadAsStringAsync()).ShouldBe(payload);
    }

    [Fact]
    public async Task GdprDisclosureReport_NullCallerContext_FailsClosedWithoutAuditQuery()
    {
        var auditRepo = new RecordingAuditRepo();
        var service = new GdprDisclosureReportService(
            new InMemoryDataOwnershipRepository(),
            auditRepo,
            new InMemoryTableMetadataRepository());

        var table = new TableIdentifier("finance", "dbo", "salary");

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            service.GetGdprDataDisclosureReportAsync(table, new Sid("S-1-5-21-EMP"), 365, callerContext: null));

        auditRepo.QueryCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task HitLApproval_ApproverWithNullTenantId_IsTreatedAsNotFound()
    {
        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions { Enabled = true, ApprovalTimeoutSeconds = 30, RequireDifferentApprover = true, RequireTotp2Fa = false }
        });
        var service = new HitLStepUpApprovalService(options, NullLogger<HitLStepUpApprovalService>.Instance);
        var table = new TableIdentifier("finance", "public", "transfers");

        var requestTask = service.RequestStepUpApprovalAsync("transfer", "tenant-a", "S-1-5-21-REQ", table);
        var ticket = service.GetPendingTickets("tenant-a").ShouldHaveSingleItem();

        var nullTenantApprover = new HitLApproverContext("S-1-5-21-OTHER", ["S-1-5-21-OTHER"], TenantId: null, IsCrossTenantAdmin: false);
        var approveResult = await service.ApproveStepUpRequestAsync(ticket.ApprovalId, nullTenantApprover);
        var rejectResult = await service.RejectStepUpRequestAsync(ticket.ApprovalId, nullTenantApprover, "tamper");

        approveResult.IsApproved.ShouldBeFalse();
        approveResult.Message.ShouldBe("Approval ticket not found.");
        rejectResult.Message.ShouldBe("Approval ticket not found.");
        (await service.GetTicketAsync(ticket.ApprovalId))!.Status.ShouldBe(HitLApprovalStatus.Pending);

        await service.RejectStepUpRequestAsync(ticket.ApprovalId, new HitLApproverContext("S-1-5-21-STEWARD", ["S-1-5-21-STEWARD"], "tenant-a"), "cleanup");
        await requestTask;
    }

    [Fact]
    public async Task AccessDecisionPoint_ProductUnderConsentBypass_NeverLeavesPiiMaskedColumnClear()
    {
        var harness = new ProductHarness();
        var admin = harness.Caller(ProductHarness.Admin, admin: true);
        var product = await harness.Service.PutAsync(harness.Put(admin, "SELECT c.id, c.email, c.segment FROM crm.public.customers c", ProductHarness.Bob));
        var now = DateTimeOffset.UtcNow;
        var metadata = ProductCatalogProjector.Project(product, now, now);

        var options = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true }
        });

        var pdp = new AccessDecisionPoint(
            options,
            new Autheris.Application.Services.ConsentResolutionService(logger: NullLogger<Autheris.Application.Services.ConsentResolutionService>.Instance),
            new InMemoryConsentRepository(),
            NullConsentCacheService.Instance,
            mandatoryFilters: new NoOpMandatoryRowFilterResolver(),
            productRepository: harness.Store);

        var subject = new SecurityPrincipalContext
        {
            IsAuthenticated = true,
            UserSid = ProductHarness.Carol,
            ResolvedTenant = ProductHarness.Tenant,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };

        var decision = await pdp.DecideAsync(new AccessRequest(subject, metadata, AccessOperation.Read, AccessTransport.Rest));

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
        decision.GetEffectiveColumnAccess("email", metadata).ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task TableAccessDecision_And_QueryAnalysis_ColumnLookupIsCaseInsensitive()
    {
        var table = new TableIdentifier("sales", "dbo", "customers");
        var caseSensitive = new Dictionary<string, ColumnAccessLevel>(StringComparer.Ordinal)
        {
            ["email"] = ColumnAccessLevel.Mask
        };
        var decision = TableAccessDecision.Allowed(table, caseSensitive, hasUnconstrainedColumnAllow: true);

        decision.GetColumnAccess("EMAIL").ShouldBe(ColumnAccessLevel.Mask);
        decision.GetColumnAccess("Email").ShouldBe(ColumnAccessLevel.Mask);

        var metaRepo = new InMemoryTableMetadataRepository();
        await metaRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = table,
            Table = new Table { SourceName = "sales", SchemaName = "dbo", TableName = "customers", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "email", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "MASK_EMAIL" } }
        });

        var otherTable = new TableIdentifier("hr", "dbo", "employees");
        await metaRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = otherTable,
            Table = new Table { SourceName = "hr", SchemaName = "dbo", TableName = "employees", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "cust_id", DataType = "integer" }
            ]
        });

        var consentRepo = new InMemoryConsentRepository();
        var userSid = new Sid("S-1-5-21-ANALYST");
        var tenant = new TenantId("tenant-a");
        foreach (var tbl in new[] { table, otherTable })
        {
            consentRepo.Add(new Consent
            {
                TenantId = tenant,
                TableIdentifier = tbl,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                Effect = ConsentEffect.Allow,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(10),
                ColumnRules = tbl == table
                    ? [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }, new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }]
                    : [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }, new ConsentColumnRule { ColumnName = "cust_id", AccessLevel = ColumnAccessLevel.Clear }]
            });
        }

        var analysis = new QueryAnalysisService(metaRepo, consentRepo, new InMemoryAuditLogRepository());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userSid.Value),
            new Claim(ClaimTypes.PrimarySid, userSid.Value),
            new Claim("tenant_id", tenant.Value)
        ], "Test"));

        var report = await analysis.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT c.id FROM sales.dbo.customers c JOIN hr.dbo.employees e ON c.id = e.cust_id WHERE c.EMAIL = 'x@y.z'")],
            principal);

        var fedTable = report.Federation!.Tables.First(t => t.Table.Contains("customers", StringComparison.OrdinalIgnoreCase));
        fedTable.PushedPredicates.ShouldBeEmpty();
        fedTable.ResidualPredicates.ShouldContain(r => string.Equals(r.Column, "email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ObjectPattern_And_TreeSqlCompiler_RejectTrailingNewlineBypass()
    {
        var wildcard = ObjectPattern.Parse("sales.dbo.ord*");
        wildcard.MatchesObject(new TableIdentifier("sales", "dbo", "orders")).ShouldBeTrue();
        ObjectPattern.Parse("sales.dbo.(orders)").MatchesColumn("id").ShouldBeTrue();

        // Regex $ anchor previously allowed a trailing '\n' in matched input
        var exprPattern = ObjectPattern.Parse("sales.dbo.(orders).*");
        exprPattern.MatchesObject(new TableIdentifier("sales", "dbo", "orders")).ShouldBeTrue();
        var colPattern = ObjectPattern.Parse("sales.dbo.orders.(col_a)");
        colPattern.MatchesColumn("col_a\n").ShouldBeFalse();
        var regexWildPattern = ObjectPattern.Parse("sales.dbo.orders.(col_.*)");
        regexWildPattern.MatchesColumn("col_a\n").ShouldBeFalse();
        var wildColPattern = ObjectPattern.Parse("sales.dbo.orders.col_*");
        wildColPattern.MatchesColumn("col_a\n").ShouldBeFalse();

        // TreeSqlCompiler SafeNameRegex with $ previously matched "id\n" and unvalidated response keys
        var tbl = new TableIdentifier("blog", "main", "items");
        var metaWithNewlinePk = new TableMetadata
        {
            Identifier = tbl,
            Table = new Table { SourceName = "blog", SchemaName = "main", TableName = "items", SourceType = "PostgreSql", IsActive = true },
            PrimaryKeyColumns = ["id\n"],
            Columns = [new TableColumn { ColumnName = "status", DataType = "varchar" }, new TableColumn { ColumnName = "id\n", DataType = "integer" }]
        };
        var decision = TableAccessDecision.Allowed(tbl, new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase), hasUnconstrainedColumnAllow: true);
        var badPkAccess = new Dictionary<TableIdentifier, TreeTableAccess> { [tbl] = new(metaWithNewlinePk, decision) };

        Should.Throw<GatewayInvalidQueryException>(() =>
            TreeSqlCompiler.Compile(new TreeQueryNode(tbl, ["status"]), badPkAccess, DatabaseDialect.PostgreSql));

        var meta = new TableMetadata
        {
            Identifier = tbl,
            Table = new Table { SourceName = "blog", SchemaName = "main", TableName = "items", SourceType = "PostgreSql", IsActive = true },
            PrimaryKeyColumns = ["id"],
            Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }, new TableColumn { ColumnName = "parent_id", DataType = "integer" }]
        };
        var access = new Dictionary<TableIdentifier, TreeTableAccess> { [tbl] = new(meta, decision) };

        var badRel = new TreeRelationNode("items\n", ["id"], ["parent_id"], IsList: true, new TreeQueryNode(tbl, ["id"]));
        Should.Throw<GatewayInvalidQueryException>(() =>
            TreeSqlCompiler.Compile(new TreeQueryNode(tbl, ["id"]) { Relations = [badRel] }, access, DatabaseDialect.PostgreSql));

        var validSetRel = new TreeRelationNode("child_items", ["id"], ["parent_id"], IsList: true, new TreeQueryNode(tbl, ["id"]));
        var compiled = TreeSqlCompiler.Compile(
            new TreeQueryNode(tbl, ["id"]) { Relations = [validSetRel] },
            access,
            DatabaseDialect.PostgreSql);
        compiled.Sql.ShouldContain("'child_items'");
    }

    [Fact]
    public void DomainTypes_AreHardenedAgainstPostConstructionMutationAndBypass()
    {
        typeof(TableAccessDecision).IsSealed.ShouldBeTrue();

        foreach (var propName in new[] { nameof(TableAccessDecision.RowPolicy), nameof(TableAccessDecision.MandatoryRowPolicy), nameof(TableAccessDecision.RowPolicyBeforeVirtualFilters), nameof(TableAccessDecision.RowPoliciesAfterVirtualFilters) })
        {
            var prop = typeof(TableAccessDecision).GetProperty(propName)!;
            prop.SetMethod.ShouldNotBeNull();
            prop.SetMethod!.IsPublic.ShouldBeFalse($"{propName} must not have a public init/set accessor.");
        }

        var tenantProp = typeof(Consent).GetProperty(nameof(Consent.TenantId))!;
        var setMethod = tenantProp.SetMethod!;
        var isInitOnly = setMethod.ReturnParameter.GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        (setMethod.IsPublic && !isInitOnly).ShouldBeFalse("Consent.TenantId must not have a mutable public setter.");

        var valid = new TableIdentifier("sales", "dbo", "orders");
        Should.Throw<ArgumentException>(() => valid with { Domain = "" });
        Should.Throw<ArgumentException>(() => valid with { Schema = "a.b" });
        Should.Throw<ArgumentException>(() => valid with { TableName = "orders\n" });
        (valid with { Domain = "  crm  " }).Domain.ShouldBe("crm");
    }

    [Fact]
    public async Task GatewayExecutionService_HttpDeclarativeSource_IgnoresPushdownAndMaskingAttestations()
    {
        var tableId = new TableIdentifier("crm_api", "public", "customers");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceName = "crm_api",
                SchemaName = "public",
                TableName = "customers",
                SourceType = "HttpDeclarative",
                DataSourceType = DataSourceType.HttpDeclarative,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "varchar" },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "email", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule { RuleType = "MASK_EMAIL" }
            }
        };

        var metaRepo = new InMemoryTableMetadataRepository();
        await metaRepo.UpsertTableMetadataAsync(metadata);

        var decision = TableAccessDecision.Allowed(
            tableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["region"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Mask
            },
            rowFilterSql: "region = 'EU'");

        var connector = new AttestingHttpStubConnector(
            [
                new Dictionary<string, object?> { ["id"] = "1", ["region"] = "EU", ["email"] = "alice@example.com" },
                new Dictionary<string, object?> { ["id"] = "2", ["region"] = "US", ["email"] = "bob@example.com" }
            ]);
        var registry = new SingleConnectorRegistry(tableId, connector);

        var service = new GatewayExecutionService(
            metaRepo,
            new InMemoryConsentRepository(),
            new InMemoryAuditLogRepository(),
            new Autheris.Application.Services.ConsentResolutionService(),
            NullConsentCacheService.Instance,
            new ColumnMaskingProvider(),
            options: Options.Create(new GatewayOptions()),
            dataSourceExecutors: [new DummyHttpExecutor()],
            connectorRegistry: registry,
            transportGate: new AllowAllTransportGate(),
            accessDecisionPoint: new FixedPdp(decision));

        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
            new Claim("tenant_id", "tenant-a")
        ], "Test"));

        var (rows, _) = await service.ExecuteTableQueryAsync(principal, tableId);

        rows.ShouldHaveSingleItem();
        rows[0]["id"].ShouldBe("1");
        rows[0]["email"].ShouldBe("***@***");
    }

    [Fact]
    public void GovernedTableReadPlanner_PlanKeysetStream_RequiresExplicitClearOnPrimaryKey()
    {
        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "sales", SchemaName = "dbo", TableName = "orders", SourceType = "PostgreSql", IsActive = true },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "status", DataType = "varchar" }
            ]
        };

        var decision = TableAccessDecision.Allowed(
            tableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.RowDependent,
                ["status"] = ColumnAccessLevel.Clear
            });

        var planner = new GovernedTableReadPlanner();
        var request = new BulkTableStreamRequest(Select: ["id", "status"], Limit: 10);

        Should.Throw<KeysetStreamingUnavailableException>(() =>
            planner.PlanKeysetStream(request, metadata, decision, "tenant-a", "S-1-5-21-CALLER"));

        var clearPkDecision = TableAccessDecision.Allowed(
            tableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["status"] = ColumnAccessLevel.RowDependent
            });
        var filteredRequest = new BulkTableStreamRequest(
            Select: ["id"],
            Filter: new TypedFilterNode(Field: "status", Op: "eq", Value: System.Text.Json.JsonSerializer.SerializeToElement("open")),
            Limit: 10);
        Should.Throw<ArgumentException>(() =>
            planner.PlanKeysetStream(filteredRequest, metadata, clearPkDecision, "tenant-a", "S-1-5-21-CALLER"));
    }

    private sealed class InMemoryVfRepo : IVirtualFilterRepository
    {
        public Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new VirtualFilterSnapshot(1, [], []));
        public Task<long> GetGenerationAsync(CancellationToken ct = default) => Task.FromResult(1L);
        public Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingAuditRepo : IAuditLogRepository
    {
        public int QueryCallCount { get; private set; }
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default)
        {
            QueryCallCount++;
            return Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        }
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FixedPdp(TableAccessDecision decision) : IAccessDecisionPoint
    {
        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(decision);

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            SecurityPrincipalContext subject,
            IReadOnlyList<TableMetadata> objects,
            AccessOperation operation,
            AccessTransport transport,
            CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<TableAccessDecision>>(objects.Select(_ => decision).ToList());

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(new AccessExplanation(decision.IsAllowed, [], [], new EffectiveGroupsExplanation([], "Stub", "Complete"), [], 0));
    }

    private sealed class SingleConnectorRegistry(TableIdentifier target, IAutherisConnector connector) : IAutherisConnectorRegistry
    {
        public void RegisterConnector(string catalogName, IAutherisConnector c) { }
        public void RegisterFactory(IAutherisConnectorFactory factory) { }
        public IAutherisConnector? GetConnector(string catalogName) => null;
        public IReadOnlyCollection<IAutherisConnector> GetAllConnectors() => [connector];
        public bool TryGetConnectorForTable(TableIdentifier table, out IAutherisConnector? found)
        {
            found = table == target ? connector : null;
            return found != null;
        }
    }

    private sealed class AttestingHttpStubConnector(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        : IAutherisConnector, IConnectorMetadata, IConnectorSplitManager, IConnectorRecordSource
    {
        public string ConnectorId => "http-stub";
        public string ConnectorType => "HttpDeclarative";
        public ConnectorCapabilities Capabilities => ConnectorCapabilities.DefaultHttp;
        public IConnectorMetadata Metadata => this;
        public IConnectorSplitManager SplitManager => this;
        public IConnectorRecordSource RecordSource => this;

        public Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<TableIdentifier>> ListTablesAsync(string? schema = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableIdentifier>>([]);

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(null);

        public Task<IReadOnlyList<ConnectorSplit>> GetSplitsAsync(TableMetadata table, ConnectorSessionContext session, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConnectorSplit>>([ConnectorSplit.Default()]);

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadSplitAsync(
            ConnectorSplit split,
            ConnectorSessionContext session,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            session.Items[GovernedConnectorReader.RlsPushdownExecutedKey] = true;
            session.Items[GovernedConnectorReader.InDbColumnMaskingExecutedKey] = true;
            foreach (var row in rows)
            {
                yield return row;
            }
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadBatchAsync(
            ConnectorSplit split,
            ConnectorSessionContext session,
            CancellationToken ct = default)
        {
            session.Items[GovernedConnectorReader.RlsPushdownExecutedKey] = true;
            session.Items[GovernedConnectorReader.InDbColumnMaskingExecutedKey] = true;
            return Task.FromResult(rows);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummyHttpExecutor : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.HttpDeclarative;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            DataSourceExecutionContext context,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
    }

    private sealed class NonSeekableMemoryStream(byte[] bytes) : System.IO.MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
    }
}
