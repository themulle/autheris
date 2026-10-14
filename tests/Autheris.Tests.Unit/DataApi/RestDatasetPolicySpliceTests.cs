namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Autheris.Api.Mcp;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// WP-X0n (G-5, SEC-X1-17): REST datasets and MCP dataset tools no longer splice the decision's row-filter text into the
/// user SQL. The rewriter (WebSQL branch) or the staging service (cross-source branch) applies the policy exactly once,
/// from a decision computed from the same inputs.
/// </summary>
public sealed class RestDatasetPolicySpliceTests
{
    private const string TenantValue = "tenant_a";
    private static readonly TableIdentifier Customers = new("default", "dbo", "customers");

    private static ClaimsPrincipal Alice(params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-REST-ALICE"),
            new(ClaimTypes.Role, "analyst"),
            new("tenant_id", TenantValue),
            new("purpose", "marketing")
        };
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static TableMetadata CustomersMeta() => new()
    {
        Identifier = Customers,
        Table = new Table { TableName = "customers", SchemaName = "dbo", SourceType = "SqlServer", DataSourceType = DataSourceType.Sql, IsActive = true },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "varchar" },
            new TableColumn { ColumnName = "region", DataType = "varchar" }
        ],
        PrimaryKeyColumns = ["id"]
    };

    private static ConsentRowFilter RegionEu() => new() { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EU\"" };

    private static Consent AllowConsent(string? purpose = null, params ConsentRowFilter[] filters) => new()
    {
        TableIdentifier = Customers,
        TenantId = new TenantId(TenantValue),
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("S-1-5-21-REST-ALICE"),
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = filters
    };

    private static TableAccessDecision SqlServerPolicyDecision() =>
        TableAccessDecision.Allowed(
            Customers,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["name"] = ColumnAccessLevel.Clear,
                ["region"] = ColumnAccessLevel.Clear
            },
            rowFilterSql: "[region] = @p0",
            hasUnconstrainedColumnAllow: true);

    private sealed class Fixture
    {
        public ITableMetadataRepository Metadata { get; } = Substitute.For<ITableMetadataRepository>();
        public IUnifiedPolicyDecisionPoint Pdp { get; } = Substitute.For<IUnifiedPolicyDecisionPoint>();
        public IGovernedSqlExecutionService Sql { get; } = Substitute.For<IGovernedSqlExecutionService>();
        public IFederatedQueryExecutionService Federated { get; } = Substitute.For<IFederatedQueryExecutionService>();
        public List<GovernedSqlQueryRequest> Captured { get; } = [];

        public Fixture(TableAccessDecision? decision = null)
        {
            var meta = CustomersMeta();
            Metadata.GetTableMetadataAsync(Customers, Arg.Any<CancellationToken>()).Returns(meta);
            Pdp.EvaluateAccessAsync(Customers, meta, Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
                .Returns(decision ?? SqlServerPolicyDecision());
            Sql.ExecuteQueryBufferedAsync(Arg.Do<GovernedSqlQueryRequest>(Captured.Add), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
                .Returns(new GovernedSqlResult("q", ["id"], [], 0, 1));
        }

        public GovernedDataQueryService Service(IFederatedQueryExecutionService? federated = null) =>
            new(Metadata, Pdp, Sql, federated ?? Federated, Substitute.For<IColumnMaskingProvider>());
    }

    [Fact]
    public async Task RestDataset_SqlServerPolicy_NoBracketTextInInput()
    {
        var fx = new Fixture();

        await fx.Service().ExecuteQueryAsync(
            new DatasetQueryRequest(Customers, SelectColumns: ["id", "name"], Limit: 10, Transport: "Rest"),
            new RequestContext(Alice(), new TenantId(TenantValue)));

        var sql = fx.Captured.ShouldHaveSingleItem().Sql;
        sql.ShouldNotContain("[");
        sql.ShouldNotContain("]");
        sql.ShouldNotContain("@p0");
        sql.ShouldNotContain("region", Case.Insensitive);
        fx.Captured[0].Parameters.ShouldBeNull();
    }

    [Fact]
    public async Task RestDataset_ColumnsFilterAndOrderBy_AreQuotedIdentifiers()
    {
        var fx = new Fixture();

        await fx.Service().ExecuteQueryAsync(
            new DatasetQueryRequest(Customers, SelectColumns: ["id", "name"], FilterExpression: "name eq 'Anna' and id gt 5", OrderBy: "name desc", Limit: 10),
            new RequestContext(Alice(), new TenantId(TenantValue)));

        fx.Captured.ShouldHaveSingleItem().Sql.ShouldBe(
            "SELECT \"id\", \"name\" FROM default.dbo.customers WHERE \"name\" = 'Anna' AND \"id\" > 5 ORDER BY \"name\" DESC LIMIT 11");
    }

    [Theory]
    [InlineData("name; DROP TABLE x")]
    [InlineData("name desc, (select 1)")]
    [InlineData("name sideways")]
    public async Task RestDataset_MalformedOrderBy_IsRejected(string orderBy)
    {
        var fx = new Fixture();

        await Should.ThrowAsync<ArgumentException>(() => fx.Service().ExecuteQueryAsync(
            new DatasetQueryRequest(Customers, SelectColumns: ["id"], OrderBy: orderBy, Limit: 10),
            new RequestContext(Alice(), new TenantId(TenantValue))));

        fx.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task RestDataset_NonCanonicalClusterAdminRole_IsDeniedBecauseDecisionInputsDiffer()
    {
        // The unified PDP drops a non-canonical ClusterAdmin role, the WebSQL rewriter keeps it as an ordinary role. Without
        // the splice (which used to AND both decisions) the rewriter's decision alone would apply, so the request fails closed.
        var fx = new Fixture();
        // A ForwardAuth identity is never a canonical cluster admin.
        var user = new ClaimsPrincipal(new ClaimsIdentity(Alice().Claims.Append(new Claim(ClaimTypes.Role, "ClusterAdmin")), ClusterAdminPolicy.ForwardAuthScheme));

        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Service().ExecuteQueryAsync(
            new DatasetQueryRequest(Customers, SelectColumns: ["id"], Limit: 10),
            new RequestContext(user, new TenantId(TenantValue))));

        fx.Captured.ShouldBeEmpty();
    }

    // ---- Same decision inputs as the rewriter (SEC-X1-17) ------------------------------------------------------------

    private sealed class Recorded
    {
        public List<SecurityEvaluationContext> Contexts { get; } = [];
        public List<(Sid User, IReadOnlySet<Sid> Groups, IReadOnlySet<string> Roles, TableIdentifier Table, IReadOnlyList<Consent> Consents)> Resolutions { get; } = [];
    }

    private static (IConsentRepository Repo, IConsentResolutionService Resolution, IPolicyEnforcementService Casbin, Recorded Rec) Recorders(params Consent[] consents)
    {
        var rec = new Recorded();
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents));

        var real = new ConsentResolutionService();
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(), Arg.Any<IReadOnlySet<Sid>?>())
            .Returns(ci =>
            {
                rec.Resolutions.Add((ci.ArgAt<Sid>(0), ci.ArgAt<IReadOnlySet<Sid>>(1), ci.ArgAt<IReadOnlySet<string>>(2), ci.ArgAt<TableIdentifier>(3), ci.ArgAt<IReadOnlyList<Consent>>(4)));
                return real.ResolveAccess(ci.ArgAt<Sid>(0), ci.ArgAt<IReadOnlySet<Sid>>(1), ci.ArgAt<IReadOnlySet<string>>(2), ci.ArgAt<TableIdentifier>(3), ci.ArgAt<IReadOnlyList<Consent>>(4), ci.ArgAt<DatabaseDialect>(5), ci.ArgAt<IReadOnlySet<Sid>?>(6));
            });

        var casbin = Substitute.For<IPolicyEnforcementService>();
        casbin.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        casbin.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                rec.Contexts.Add(ci.Arg<SecurityEvaluationContext>());
                return new ValueTask<TableAccessDecision>(TableAccessDecision.Allowed(Customers, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
            });
        return (repo, resolution, casbin, rec);
    }

    private static GatewayOptions Options_() => new()
    {
        WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 500 }
    };

    [Fact]
    public async Task RestDataset_DecisionInputsEqualRewriterInputs()
    {
        var consents = new[] { AllowConsent(purpose: "marketing", RegionEu()) };
        var (repo, resolution, casbin, rec) = Recorders(consents);
        var options = Options_();
        var user = Alice();
        var meta = CustomersMeta();

        // REST side: the unified PDP as GovernedDataQueryService calls it.
        var pdp = new UnifiedPolicyDecisionPoint(
            repo, resolution, Substitute.For<IConsentCacheService>(), Options.Create(options),
            NullLogger<UnifiedPolicyDecisionPoint>.Instance, casbin);
        var restDecision = await pdp.EvaluateAccessAsync(
            Customers, meta, SecurityPrincipalContext.FromPrincipal(user, new TenantId(TenantValue)), ["id", "name"], CancellationToken.None);
        var restResolution = rec.Resolutions.Single();
        var restContext = rec.Contexts.Single();
        rec.Resolutions.Clear();
        rec.Contexts.Clear();

        // WebSQL side: the rewriter that applies the policy to the SQL the REST service builds.
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(meta);
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var service = new GovernedSqlExecutionService(
            Options.Create(options), global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, executionGuard: GovernedTestSupport.Guard, policyEnforcement: casbin, consentResolution: resolution, tableRepository: tableRepo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(tableRepo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            connectionFactory: null, clientIpResolver: null, environment: env, logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: repo, secretProvider: null);
        await service.RewriteSqlAsync("SELECT \"id\", \"name\" FROM default.dbo.customers LIMIT 11", user, new TenantId(TenantValue));
        var rewriterResolution = rec.Resolutions.Single();
        var rewriterContext = rec.Contexts.Single();

        restResolution.User.ShouldBe(rewriterResolution.User);
        restResolution.Table.ShouldBe(rewriterResolution.Table);
        restResolution.Groups.OrderBy(s => s.Value).ShouldBe(rewriterResolution.Groups.OrderBy(s => s.Value));
        restResolution.Roles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ShouldBe(rewriterResolution.Roles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
        restResolution.Consents.ShouldBe(rewriterResolution.Consents);

        restContext.UserSid.ShouldBe(rewriterContext.UserSid);
        restContext.Tenant.ShouldBe(rewriterContext.Tenant);
        restContext.TargetTable.ShouldBe(rewriterContext.TargetTable);
        restContext.PurposeId.ShouldBe("marketing");
        restContext.PurposeId.ShouldBe(rewriterContext.PurposeId);
        restContext.ClientIp.ShouldBe(rewriterContext.ClientIp);
        restContext.Attributes!.Where(a => a.Key is not ("gql.action" or "action")).OrderBy(a => a.Key).ShouldBe(
            rewriterContext.Attributes!.Where(a => a.Key is not ("gql.action" or "action")).OrderBy(a => a.Key));
        restDecision.CombinedRowFilterSql.ShouldNotBeNullOrWhiteSpace();
    }

    // ---- Policy applied once on the WebSQL branch -------------------------------------------------------------------

    [Fact]
    public async Task RestDataset_ResultEqualsSplicedResult_OnFixture()
    {
        // The SQL the REST service emits, run through the real rewriter with a consent row filter, carries the policy
        // predicate exactly once: the rewriter's own injection, no second copy from the splice.
        var (repo, resolution, casbin, _) = Recorders(AllowConsent(null, RegionEu()));
        var meta = CustomersMeta();
        var consentDecision = new ConsentResolutionService().ResolveAccess(
            new Sid("S-1-5-21-REST-ALICE"), new HashSet<Sid>(), new HashSet<string>(), Customers, [AllowConsent(null, RegionEu())], DatabaseDialect.SqlServer);
        var fx = new Fixture(consentDecision);

        await fx.Service().ExecuteQueryAsync(
            new DatasetQueryRequest(Customers, SelectColumns: ["id", "name"], Limit: 10),
            new RequestContext(Alice(), new TenantId(TenantValue)));
        var emitted = fx.Captured.ShouldHaveSingleItem().Sql;

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(meta);
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var service = new GovernedSqlExecutionService(
            Options.Create(Options_()), global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, executionGuard: GovernedTestSupport.Guard, policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance, consentResolution: resolution, tableRepository: tableRepo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(tableRepo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            connectionFactory: null, clientIpResolver: null, environment: env, logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: repo, secretProvider: null);

        var rewritten = await service.CompileAsync(emitted, Alice(), new TenantId(TenantValue));
        var baseline = await service.CompileAsync("SELECT \"id\", \"name\" FROM default.dbo.customers ORDER BY \"id\" LIMIT 11", Alice(), new TenantId(TenantValue));

        // X1: the policy value is a bound parameter. It appears exactly once (no second copy from a splice) and never in the text.
        rewritten.CountOfValue(TrinoSqlEngine.Ast.Emit.ParameterOrigin.Policy, "EU").ShouldBe(1);
        rewritten.Sql.ShouldNotContain("'EU'");
        rewritten.Sql.ShouldBe(baseline.Sql);
    }

    // ---- MCP dataset tool: same SQL and rows as REST -----------------------------------------------------------------

    [Fact]
    public async Task McpDatasetTool_SameRows()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Anna" },
            new Dictionary<string, object?> { ["id"] = 2, ["name"] = "Ben" }
        };
        var rest = new Fixture();
        rest.Sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                rest.Captured.Add(ci.Arg<GovernedSqlQueryRequest>());
                return new GovernedSqlResult("q", ["id", "name"], rows, 2, 1);
            });
        var restEnvelope = await rest.Service().ExecuteQueryAsync(
            new DatasetQueryRequest(Customers, ["id", "name"], "id gt 0", "name", 10, 0, SqlTransports.Rest),
            new RequestContext(Alice(), new TenantId(TenantValue)));

        var mcp = new Fixture();
        mcp.Sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                mcp.Captured.Add(ci.Arg<GovernedSqlQueryRequest>());
                return new GovernedSqlResult("q", ["id", "name"], rows, 2, 1);
            });
        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(), new Services(mcp), Substitute.For<ICatalogDiscoveryService>(),
            Substitute.For<IApiDispatcherService>(), Substitute.For<ILineageGraphStore>(), Substitute.For<ITableMetadataRepository>());
        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.QueryDataset);
        var session = new McpSessionContext("s", "S-1-5-21-REST-ALICE", TenantValue, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "S-1-5-21-REST-ALICE", ["analyst"], [], "10.0.0.1", false, new Dictionary<string, string> { ["purpose"] = "marketing" });

        var json = await handler.ExecuteToolAsync(tool, JsonSerializer.Serialize(new
        {
            dataset = "default.dbo.customers", select = new[] { "id", "name" }, filter = "id gt 0", orderBy = "name", limit = 10
        }), session);

        // Same SQL up to the transport tag: neither path carries policy text, so both depend on the one rewriter decision.
        var restSql = rest.Captured.Last().Sql;
        var mcpSql = mcp.Captured.Last().Sql;
        mcpSql.ShouldBe(restSql);
        mcpSql.ShouldNotContain("[");
        mcpSql.ShouldNotContain("@p0");
        mcp.Captured.Last().Transport.ShouldBe(SqlTransports.Mcp);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(restEnvelope.Count);
        doc.RootElement.GetProperty("data").GetArrayLength().ShouldBe(2);
    }

    private sealed class Services(Fixture fx) : Autheris.Application.Data.Interfaces.IGovernedDataQueryService
    {
        public Task<DatasetQueryEnvelope> ExecuteQueryAsync(DatasetQueryRequest request, RequestContext context, CancellationToken ct = default) =>
            fx.Service().ExecuteQueryAsync(request, context, ct);
    }

    // ---- Cross-source branch: the staging service still applies its own decision ------------------------------------

    private static readonly TableMetadata CrmCustomers = HttpTable("crm_api", "public", "customers", "id", "name", "status");

    private static FederatedServiceHarness.Pipeline CrossSourcePipeline() =>
        FederatedServiceHarness.Build(
            [
                (CrmCustomers, new StubTableConnector("crm_api",
                [
                    Row(("id", "1"), ("name", "Alpha"), ("status", "OPEN")),
                    Row(("id", "2"), ("name", "Beta"), ("status", "CLOSED")),
                    Row(("id", "3"), ("name", "Gamma"), ("status", "OPEN"))
                ]))
            ],
            FederatedServiceHarness.Consents(FederatedServiceHarness.Allow(
                CrmCustomers.Identifier,
                new ConsentRowFilter { ColumnName = "status", Operator = "EQ", ValueType = "string", ValueJson = "\"OPEN\"" })));

    [Fact]
    public async Task CrossSourceDataset_RowFilterStillApplied_AfterSpliceRemoval()
    {
        var pipeline = CrossSourcePipeline();
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(CrmCustomers.Identifier, Arg.Any<CancellationToken>()).Returns(CrmCustomers);
        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        // The REST decision carries the SAME consent filter as text, which the service must not splice any more.
        pdp.EvaluateAccessAsync(CrmCustomers.Identifier, CrmCustomers, Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(CrmCustomers.Identifier, new Dictionary<string, ColumnAccessLevel>(), "[status] = @p0", hasUnconstrainedColumnAllow: true));
        GovernedSqlQueryRequest? seen = null;
        var federated = Substitute.For<IFederatedQueryExecutionService>();
        federated.ExecuteQueryBufferedAsync(Arg.Do<GovernedSqlQueryRequest>(r => seen = r), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(ci => pipeline.Service.ExecuteQueryBufferedAsync(ci.Arg<GovernedSqlQueryRequest>(), ci.Arg<ClaimsPrincipal>(), ci.Arg<TenantId>(), ci.Arg<CancellationToken>()));
        var service = new GovernedDataQueryService(metadata, pdp, Substitute.For<IGovernedSqlExecutionService>(), federated, Substitute.For<IColumnMaskingProvider>());

        var envelope = await service.ExecuteQueryAsync(
            new DatasetQueryRequest(CrmCustomers.Identifier, SelectColumns: ["id", "name", "status"], Limit: 10, Transport: "WebSql"),
            new RequestContext(User(FederatedServiceHarness.AliceSid), Tenant));

        seen.ShouldNotBeNull();
        seen.Sql.ShouldNotContain("[");
        envelope.Data.Select(r => r["id"]?.ToString()).OrderBy(x => x).ShouldBe(["1", "3"]);
    }

    [Fact]
    public async Task RestDataset_TenantBRowsAbsent_OnEveryBranch()
    {
        // Cross-source branch: the consent filter keeps tenant A rows of an HTTP table that returns both tenants.
        var table = HttpTable("crm_api", "public", "customers", "id", "tenant_id");
        var pipeline = FederatedServiceHarness.Build(
            [
                (table, new StubTableConnector("crm_api",
                [
                    Row(("id", "1"), ("tenant_id", FederatedVfPipelineHarness.TenantValue)),
                    Row(("id", "2"), ("tenant_id", "tenant_b")),
                    Row(("id", "3"), ("tenant_id", FederatedVfPipelineHarness.TenantValue))
                ]))
            ],
            FederatedServiceHarness.Consents(FederatedServiceHarness.Allow(
                table.Identifier,
                new ConsentRowFilter { ColumnName = "tenant_id", Operator = "EQ", ValueType = "string", ValueJson = "\"" + FederatedVfPipelineHarness.TenantValue + "\"" })));
        var crossRows = await pipeline.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT \"id\", \"tenant_id\" FROM crm_api.public.customers ORDER BY \"id\" LIMIT 11", Transport: "WebSql"),
            User(FederatedServiceHarness.AliceSid), Tenant);
        crossRows.Rows.Select(r => r["tenant_id"]?.ToString()).ShouldAllBe(t => t == FederatedVfPipelineHarness.TenantValue);
        crossRows.Rows.Count.ShouldBe(2);

        // Single-source branch: the rewriter injects the tenant predicate (tenant column required) into the emitted SQL.
        var baseMeta = CustomersMeta();
        var meta = baseMeta with { Columns = [.. baseMeta.Columns, new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }] };
        var fx = new Fixture();
        fx.Metadata.GetTableMetadataAsync(Customers, Arg.Any<CancellationToken>()).Returns(meta);
        fx.Pdp.EvaluateAccessAsync(Customers, meta, Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(SqlServerPolicyDecision());
        await fx.Service().ExecuteQueryAsync(
            new DatasetQueryRequest(Customers, SelectColumns: ["id"], Limit: 10), new RequestContext(Alice(), new TenantId(TenantValue)));
        var emitted = fx.Captured.ShouldHaveSingleItem().Sql;

        var (repo, resolution, _, _) = Recorders(AllowConsent());
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(meta);
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 500 },
            DataSources = new SqlDataSourceOptions { RequireTenantColumn = true }
        };
        var service = new GovernedSqlExecutionService(
            Options.Create(options), global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, executionGuard: GovernedTestSupport.Guard, policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance, consentResolution: resolution, tableRepository: tableRepo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(tableRepo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            connectionFactory: null, clientIpResolver: null, environment: env, logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: repo, secretProvider: null);
        var rewritten = await service.CompileAsync(emitted, Alice(), new TenantId(TenantValue));

        rewritten.ShouldBindTenant(TenantValue);
        rewritten.Sql.ShouldNotContain("tenant_b");
        rewritten.ValuesOf(TrinoSqlEngine.Ast.Emit.ParameterOrigin.Tenant).ShouldNotContain("tenant_b");
    }

    [Theory]
    [InlineData("name eq 'name and id'", "\"name\" = 'name and id'")]
    [InlineData("NAME eq 'x' or id lt 3", "\"name\" = 'x' OR \"id\" < 3")]
    [InlineData("id eq 1e5", "\"id\" = 1e5")]
    public void TranslateFilterToTrino_QuotesColumnWordsOutsideLiterals(string filter, string expected) =>
        DataApiFilterTranslator.TranslateFilterToTrino(filter, CustomersMeta()).ShouldBe(expected);

    private static int CountOf(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
