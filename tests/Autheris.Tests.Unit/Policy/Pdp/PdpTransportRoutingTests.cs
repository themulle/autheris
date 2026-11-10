namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
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
using NSubstitute;
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
        var pdp = Substitute.For<IAccessDecisionPoint>();
        var meta = CreateTableMetadata("orders");
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(meta);

        var decision = TableAccessDecision.Allowed(
            meta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true);

        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(decision));

        var options = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true } };

        var rewriter = new GovernedSqlRewriter(
            Options.Create(options),
            tableRepository: tableRepo,
            policyFactory: GovernedTestSupport.PolicyFactory(tableRepo, Options.Create(options)),
            accessDecisionPoint: pdp);

        var user = CreateUser();
        var compiled = await rewriter.CompileAsync("SELECT id, secret FROM orders", user, Tenant);

        compiled.ShouldNotBeNull();
        await pdp.Received(1).DecideAsync(
            Arg.Is<AccessRequest>(r =>
                r.Transport == AccessTransport.WebSql &&
                r.Object.Identifier == meta.Identifier),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WebSql_RoutesDecisionsThroughPdp_WhenDeniedThrowsException()
    {
        var pdp = Substitute.For<IAccessDecisionPoint>();
        var meta = CreateTableMetadata("orders");
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(meta);

        var decision = TableAccessDecision.Denied(meta.Identifier, "Denied by PDP policy test.");

        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(decision));

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

        await pdp.Received(1).DecideAsync(
            Arg.Is<AccessRequest>(r => r.Transport == AccessTransport.WebSql),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ODataQueryPolicyEnforcer_RoutesThroughPdp()
    {
        var pdp = Substitute.For<IAccessDecisionPoint>();
        var meta = CreateTableMetadata("customers");
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(decision));

        var enforcer = new ODataQueryPolicyEnforcer(pdp);
        var user = CreateUser();

        var result = await enforcer.EnforceAsync(user, meta, ["id", "name"]);

        result.IsAllowed.ShouldBeTrue();
        await pdp.Received(1).DecideAsync(
            Arg.Is<AccessRequest>(r =>
                r.Transport == AccessTransport.OData &&
                r.Operation == AccessOperation.Read &&
                r.Object == meta &&
                r.Columns != null && r.Columns.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GraphQlDataSecurityMiddleware_RoutesThroughPdp()
    {
        var pdp = Substitute.For<IAccessDecisionPoint>();
        var meta = CreateTableMetadata("products");
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(decision));

        var user = CreateUser();
        var result = await GraphQlDataSecurityMiddleware.DecideAccessAsync(pdp, user, meta, ["id", "price"]);

        result.IsAllowed.ShouldBeTrue();
        await pdp.Received(1).DecideAsync(
            Arg.Is<AccessRequest>(r =>
                r.Transport == AccessTransport.GraphQl &&
                r.Operation == AccessOperation.Read &&
                r.Object == meta &&
                r.Columns != null && r.Columns.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GovernedProcedureExecutionService_RoutesThroughPdp()
    {
        var pdp = Substitute.For<IAccessDecisionPoint>();
        var meta = CreateTableMetadata("orders");
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(decision));

        var registry = new InMemoryProcedureRegistry();
        var invoker = Substitute.For<IProcedureInvoker>();
        var audit = Substitute.For<IAuditLogRepository>();
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(meta);

        var definition = new ProcedureDefinition("get_orders", "", "dbo.usp_GetOrders", ProcedureMode.Read, null, [], [], ProcedureRlsMode.None, "dbo.orders", [], [], 30);
        registry.Register(definition);
        registry.MarkActive("get_orders", new ProcedureValidationResult(true, [], [], ["dbo.orders"], new Dictionary<string, string>()));

        invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(["id", "secret"], [], false));

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
        await pdp.Received(1).DecideAsync(
            Arg.Is<AccessRequest>(r =>
                r.Transport == AccessTransport.Procedure &&
                r.Operation == AccessOperation.Execute),
            Arg.Any<CancellationToken>());
    }
}
