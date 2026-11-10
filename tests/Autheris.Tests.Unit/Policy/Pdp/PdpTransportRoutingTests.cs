namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.OData;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Tests.Unit.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class PdpTransportRoutingTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("user-1");

    private static ClaimsPrincipal CreateUser()
    {
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, UserSid.Value));
        identity.AddClaim(new Claim("sub", UserSid.Value));
        identity.AddClaim(new Claim("tid", Tenant.Value!));
        identity.AddClaim(new Claim("tenant_id", Tenant.Value!));
        return new ClaimsPrincipal(identity);
    }

    private static TableMetadata CreateTableMetadata(string tableName = "orders")
    {
        var id = new TableIdentifier("default", "dbo", tableName);
        var table = new Table
        {
            SourceName = "default",
            SchemaName = "dbo",
            TableName = tableName,
            IsActive = true
        };
        var columns = new List<TableColumn>
        {
            new() { ColumnName = "id", DataType = "int" },
            new() { ColumnName = "tenant_id", DataType = "varchar" },
            new() { ColumnName = "secret", DataType = "varchar" }
        };
        return new TableMetadata
        {
            Identifier = id,
            Table = table,
            Columns = columns
        };
    }

    [Fact]
    public async Task WebSql_RoutesDecisionsThroughPdp_WhenAllowed()
    {
        var meta = CreateTableMetadata("orders");
        var decision = TableAccessDecision.Allowed(
            meta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true);

        var pdp = new FakeAccessDecisionPoint(decision);
        var tableRepo = new FakeMetadataRepository(meta);
        var options = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true } };

        var rewriter = new GovernedSqlRewriter(
            Options.Create(options),
            tableRepository: tableRepo,
            policyFactory: GovernedTestSupport.PolicyFactory(tableRepo, Options.Create(options)),
            accessDecisionPoint: pdp);

        var user = CreateUser();
        var compiled = await rewriter.CompileAsync("SELECT id, secret FROM orders", user, Tenant);

        compiled.ShouldNotBeNull();
        pdp.RecordedRequests.ShouldHaveSingleItem();
        var req = pdp.RecordedRequests[0];
        req.Transport.ShouldBe(AccessTransport.WebSql);
        req.Object.Identifier.ShouldBe(meta.Identifier);
    }

    [Fact]
    public async Task WebSql_RoutesDecisionsThroughPdp_WhenDeniedThrowsException()
    {
        var meta = CreateTableMetadata("orders");
        var decision = TableAccessDecision.Denied(meta.Identifier, "Denied by PDP policy test.");
        var pdp = new FakeAccessDecisionPoint(decision);
        var tableRepo = new FakeMetadataRepository(meta);
        var options = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true } };

        var rewriter = new GovernedSqlRewriter(
            Options.Create(options),
            tableRepository: tableRepo,
            policyFactory: GovernedTestSupport.PolicyFactory(tableRepo, Options.Create(options)),
            accessDecisionPoint: pdp);

        var user = CreateUser();
        await Should.ThrowAsync<WebSqlPolicyException>(async () =>
        {
            await rewriter.CompileAsync("SELECT id, secret FROM orders", user, Tenant);
        });

        pdp.RecordedRequests.ShouldHaveSingleItem();
        pdp.RecordedRequests[0].Transport.ShouldBe(AccessTransport.WebSql);
    }

    [Fact]
    public async Task ODataQueryPolicyEnforcer_RoutesThroughPdp()
    {
        var meta = CreateTableMetadata("customers");
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var pdp = new FakeAccessDecisionPoint(decision);

        var enforcer = new ODataQueryPolicyEnforcer(pdp);
        var user = CreateUser();

        var result = await enforcer.EnforceAsync(user, meta, ["id", "name"]);

        result.IsAllowed.ShouldBeTrue();
        pdp.RecordedRequests.ShouldHaveSingleItem();
        var req = pdp.RecordedRequests[0];
        req.Transport.ShouldBe(AccessTransport.OData);
        req.Operation.ShouldBe(AccessOperation.Read);
        req.Object.ShouldBe(meta);
        req.Columns.ShouldNotBeNull();
        req.Columns.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GraphQlDataSecurityMiddleware_RoutesThroughPdp()
    {
        var meta = CreateTableMetadata("products");
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var pdp = new FakeAccessDecisionPoint(decision);

        var user = CreateUser();
        var result = await GraphQlDataSecurityMiddleware.DecideAccessAsync(pdp, user, meta, ["id", "price"]);

        result.IsAllowed.ShouldBeTrue();
        pdp.RecordedRequests.ShouldHaveSingleItem();
        var req = pdp.RecordedRequests[0];
        req.Transport.ShouldBe(AccessTransport.GraphQl);
        req.Operation.ShouldBe(AccessOperation.Read);
        req.Object.ShouldBe(meta);
        req.Columns.ShouldNotBeNull();
        req.Columns.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GovernedProcedureExecutionService_RoutesThroughPdp()
    {
        var meta = CreateTableMetadata("orders");
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var pdp = new FakeAccessDecisionPoint(decision);

        var registry = new InMemoryProcedureRegistry();
        var invoker = new FakeProcedureInvoker();
        var audit = new Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository();
        var tableRepo = new FakeMetadataRepository(meta);

        var definition = new ProcedureDefinition("get_orders", "", "dbo.usp_GetOrders", ProcedureMode.Read, null, [], [], ProcedureRlsMode.None, "dbo.orders", [], [], 30);
        registry.Register(definition);
        registry.MarkActive("get_orders", new ProcedureValidationResult(true, [], [], ["dbo.orders"], new Dictionary<string, string>()));

        var service = new GovernedProcedureExecutionService(
            registry,
            invoker,
            Options.Create(new GatewayOptions()),
            audit,
            tableRepository: tableRepo,
            concurrencyGate: new Autheris.Application.Services.TableReadConcurrencyGate(),
            accessDecisionPoint: pdp);

        var user = CreateUser();
        var result = await service.ExecuteAsync("get_orders", null, user, Tenant);

        result.ShouldNotBeNull();
        pdp.RecordedRequests.ShouldHaveSingleItem();
        var req = pdp.RecordedRequests[0];
        req.Transport.ShouldBe(AccessTransport.Procedure);
        req.Operation.ShouldBe(AccessOperation.Execute);
    }

    private sealed class FakeAccessDecisionPoint(TableAccessDecision decision) : IAccessDecisionPoint
    {
        public List<AccessRequest> RecordedRequests { get; } = [];

        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default)
        {
            RecordedRequests.Add(request);
            return new ValueTask<TableAccessDecision>(decision);
        }

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            SecurityPrincipalContext subject,
            IReadOnlyList<TableMetadata> objects,
            AccessOperation operation,
            AccessTransport transport,
            CancellationToken ct = default) =>
            new(objects.Select(_ => decision).ToList());

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeMetadataRepository(TableMetadata meta) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(meta);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([meta]);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class FakeProcedureInvoker : IProcedureInvoker
    {
        public Task<RawProcedureResult> ExecuteReadAsync(
            ProcedureDefinition definition,
            IReadOnlyDictionary<string, object?> parameters,
            ProcedureSecurityContext security,
            int timeoutSeconds,
            CancellationToken ct) =>
            Task.FromResult(new RawProcedureResult(["id", "secret"], [], false));
    }
}
