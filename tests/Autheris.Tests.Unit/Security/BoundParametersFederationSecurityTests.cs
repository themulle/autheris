namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Federation.Optimization;
using Autheris.Application.Sql.Federation.Parameters;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.ListParameters;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

[Trait("Category", "Security")]
public sealed class BoundParametersFederationSecurityTests
{
    private static (FederatedServiceHarness.Pipeline Pipeline, StubTableConnector OrdersStub, StubTableConnector CustomersStub, TableMetadata OrdersMeta, TableMetadata CustomersMeta) CreatePipeline(
        IConsentRepository? consents = null,
        bool consentBypass = false,
        Dictionary<string, MaskingRule>? masking = null)
    {
        var ordersMeta = SqlTable("crm", "public", "orders", "id", "customer_id", "amount", "status", "secret_code");
        if (masking != null)
        {
            ordersMeta = ordersMeta with { ColumnMaskingRules = masking };
        }
        var customersMeta = HttpTable("billing", "public", "customers", "id", "name");

        var ordersStub = new StubTableConnector("crm_orders", [
            Row(("id", "1"), ("customer_id", "101"), ("amount", "50"), ("status", "OPEN"), ("secret_code", "sec1")),
            Row(("id", "2"), ("customer_id", "102"), ("amount", "150"), ("status", "CLOSED"), ("secret_code", "sec2"))
        ]);

        var customersStub = new StubTableConnector("billing_customers", [
            Row(("id", "101"), ("name", "Alice")),
            Row(("id", "102"), ("name", "Bob"))
        ]);

        var cons = consents ?? FederatedServiceHarness.Consents(
            FederatedServiceHarness.Allow(ordersMeta.Identifier),
            FederatedServiceHarness.Allow(customersMeta.Identifier));

        var pipeline = FederatedServiceHarness.Build(
            [(ordersMeta, ordersStub), (customersMeta, customersStub)],
            cons,
            consentBypass: consentBypass);

        return (pipeline, ordersStub, customersStub, ordersMeta, customersMeta);
    }

    [Fact]
    public async Task T_SEC_DATA131_01_ClientNamedParameter_AcceptanceAndBinding()
    {
        var (pipeline, _, _, _, _) = CreatePipeline();
        var sql = "SELECT o.id, c.name FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id WHERE o.id = @target_id";
        var request = new GovernedSqlQueryRequest(
            sql,
            Parameters: new Dictionary<string, object?> { ["target_id"] = "2" },
            Transport: "WebSql");

        var result = await pipeline.Service.ExecuteQueryBufferedAsync(request, User(AliceSid), Tenant);

        result.Rows.Count.ShouldBe(1);
        result.Rows[0]["id"]?.ToString().ShouldBe("2");
        result.Rows[0]["name"]?.ToString().ShouldBe("Bob");
    }

    [Fact]
    public async Task T_SEC_DATA131_02_AST_ParameterizedBinding_ZeroInterpolation()
    {
        var engine = new DuckDbOlapEngine(
            new DuckDbOlapOptions { Enabled = true, MaxMemory = "256MB" },
            NullLogger<DuckDbOlapEngine>.Instance);

        // SEC-DATA131-02: Silent-NULL flaw eliminated: unbound ClientNamed slot without Compiled throws InvalidOperationException
        var unboundParam = new BoundParameter("$1", "1", 0, null, SqlParameterType.String, ParameterOrigin.ClientNamed, SourceName: "target_id");
        var sourceTable = new TableIdentifier("crm", "public", "orders");
        var sourceMeta = new TableMetadata { Identifier = sourceTable, Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }] };
        var source = new OlapTableSource(sourceTable, [new Dictionary<string, object?> { ["id"] = "1" }], sourceMeta, "s0");

        var invalidRequest = new GeneratedOlapQuery("SELECT id FROM s0 WHERE id = $1", [unboundParam], Compiled: null);
        await Should.ThrowAsync<InvalidOperationException>(() => engine.ExecuteGeneratedAsync(invalidRequest, [source]));

        // With CompiledSql and DuckDbCompiledSqlBinder, values are bound strictly via positional parameters
        var compiled = new CompiledSql(
            "SELECT \"id\" FROM \"s0\" WHERE \"id\" = $1",
            System.Collections.Immutable.ImmutableArray.Create(unboundParam),
            TargetSqlDialect.DuckDb,
            SqlStatementClass.Select,
            System.Collections.Immutable.ImmutableArray<SecurityPredicateId>.Empty,
            "v");
        var validRequest = new GeneratedOlapQuery("SELECT \"id\" FROM \"s0\" WHERE \"id\" = $1", [unboundParam], Compiled: compiled, ClientValues: new Dictionary<string, object?> { ["target_id"] = "1" });
        var result = await engine.ExecuteGeneratedAsync(validRequest, [source]);
        result.Rows.Count.ShouldBe(1);
        result.Rows[0][0]?.ToString().ShouldBe("1");
    }

    [Fact]
    public async Task T_SEC_DATA131_03_SourcePredicate_ValueParity_TableFilterClause()
    {
        var ordersMeta = SqlTable("crm", "public", "orders", "id", "customer_id", "amount");
        var ast = FastSqlEngine.Default.Parse("SELECT * FROM crm.public.orders WHERE id = __param_order_id".AsMemory()).Tree;
        var statement = new TrinoSqlEngine.Ast.Builder.SqlAstBuilder().BuildStatement(ast);
        var target = new TableAccessTarget("crm", "public", "orders", null, "crm.public.orders");

        var contract = FederatedParameterContract.Create(new Dictionary<string, object?> { ["order_id"] = "100" });

        var pushed = PushedClauseBuilder.BuildPushdown(
            statement,
            target,
            ordersMeta,
            Unrestricted(ordersMeta),
            contract,
            out var paramAudit);

        pushed.ShouldNotBeNull();
        pushed.SqlPredicate.ShouldContain("@p_pd_");
        pushed.Parameters.Count.ShouldBe(1);
        pushed.Parameters.Values.First().ShouldBe("100");
    }

    [Fact]
    public async Task T_SEC_DATA131_04_ListParameter_Expansion_AndDialectLimits()
    {
        var (pipeline, _, _, _, _) = CreatePipeline();
        var sql = "SELECT o.id, c.name FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id WHERE o.id IN (@ids)";
        var request = new GovernedSqlQueryRequest(
            sql,
            Parameters: new Dictionary<string, object?> { ["ids"] = new object[] { "1", "2" } },
            Transport: "WebSql");

        var result = await pipeline.Service.ExecuteQueryBufferedAsync(request, User(AliceSid), Tenant);
        result.Rows.Count.ShouldBe(2);

        // Clamping check: dialect limit enforcement in PushedClauseBuilder
        var ordersMeta = SqlTable("crm", "public", "orders", "id");
        var ast = FastSqlEngine.Default.Parse("SELECT * FROM crm.public.orders WHERE id IN (__param_big_list)".AsMemory()).Tree;
        var statement = new TrinoSqlEngine.Ast.Builder.SqlAstBuilder().BuildStatement(ast);
        var target = new TableAccessTarget("crm", "public", "orders", null, "crm.public.orders");

        // SqlServer max is 2100 - reserve 100 = 2000. Create list of 2005 items.
        var bigList = Enumerable.Range(1, 2005).Select(i => (object)i.ToString()).ToList();
        var contract = FederatedParameterContract.Create(new Dictionary<string, object?> { ["big_list"] = bigList }, maxListElements: 3000);

        // Should NOT be pushed due to dialect budget clamping
        var pushed = PushedClauseBuilder.BuildPushdown(
            statement,
            target,
            ordersMeta,
            Unrestricted(ordersMeta),
            contract,
            out _);

        pushed.ShouldBeNull();
    }

    [Fact]
    public async Task T_SEC_DATA131_05_FailClosed_PreReadValidation_ZeroSourceReads()
    {
        var (pipeline, ordersStub, customersStub, _, _) = CreatePipeline();
        var sql = "SELECT o.id, c.name FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id WHERE o.id = @bad_id";
        var request = new GovernedSqlQueryRequest(
            sql,
            Parameters: new Dictionary<string, object?> { ["bad_id"] = "bad\0id" },
            Transport: "WebSql");

        await Should.ThrowAsync<WebSqlSecurityException>(() =>
            pipeline.Service.ExecuteQueryBufferedAsync(request, User(AliceSid), Tenant));

        ordersStub.UpstreamCalls.ShouldBe(0);
        customersStub.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task T_SEC_DATA131_06_AntiOracle_AuthorizationPrecedence_DenialParity()
    {
        // No consents registered (unauthorized)
        var (pipeline, ordersStub, customersStub, _, _) = CreatePipeline(consents: FederatedServiceHarness.Consents());
        var sql = "SELECT o.id, c.name FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id WHERE o.id = @param";

        // Call 1: valid parameter value
        var reqValid = new GovernedSqlQueryRequest(sql, Parameters: new Dictionary<string, object?> { ["param"] = "1" }, Transport: "WebSql");
        var ex1 = await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            pipeline.Service.ExecuteQueryBufferedAsync(reqValid, User(AliceSid), Tenant));

        // Call 2: invalid parameter value (contains embedded NUL which would trigger WebSqlSecurityException)
        var reqInvalid = new GovernedSqlQueryRequest(sql, Parameters: new Dictionary<string, object?> { ["param"] = "invalid\0nul" }, Transport: "WebSql");
        var ex2 = await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            pipeline.Service.ExecuteQueryBufferedAsync(reqInvalid, User(AliceSid), Tenant));

        // Both threw WebSqlTableDeniedException; authorization strictly precedes parameter validation
        ex1.GetType().ShouldBe(ex2.GetType());
        ordersStub.UpstreamCalls.ShouldBe(0);
        customersStub.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task T_SEC_DATA131_07_AuditMinimization_ZeroValues_ErrorSanitization()
    {
        var (pipeline, _, _, _, _) = CreatePipeline();
        var sql = "SELECT o.id, c.name FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id WHERE o.id = @secret_id";
        var secretValue = "top-secret-password-12345";
        var request = new GovernedSqlQueryRequest(
            sql,
            Parameters: new Dictionary<string, object?> { ["secret_id"] = secretValue },
            Transport: "WebSql");

        await pipeline.Service.ExecuteQueryBufferedAsync(request, User(AliceSid), Tenant);

        // Check start audit
        await pipeline.Audit.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == AuditEventTypes.WebSqlCrossSourceQuery &&
                e.DetailsJson.Contains("\"name\":\"secret_id\"") &&
                e.DetailsJson.Contains("\"kind\":\"scalar\"") &&
                !e.DetailsJson.Contains(secretValue)),
            Arg.Any<CancellationToken>());

        // Check failure audit error sanitization
        var failingRequest = new GovernedSqlQueryRequest(
            "SELECT o.id, c.name FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id WHERE non_existent_column = @secret_id",
            Parameters: new Dictionary<string, object?> { ["secret_id"] = secretValue },
            Transport: "WebSql");

        await Should.ThrowAsync<Exception>(() => pipeline.Service.ExecuteQueryBufferedAsync(failingRequest, User(AliceSid), Tenant));

        await pipeline.Audit.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == AuditEventTypes.QueryExecutionError &&
                !e.DetailsJson.Contains(secretValue)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("__gql_token")]
    [InlineData("p_rls_tenant")]
    [InlineData("rls_user")]
    [InlineData("__param_internal")]
    [InlineData("p_pd_0_0")]
    public async Task T_SEC_DATA131_08_ReservedParameterNames_FailClosed(string reservedName)
    {
        var (pipeline, _, _, _, _) = CreatePipeline();
        var sql = $"SELECT o.id, c.name FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id WHERE o.id = @{reservedName}";
        var request = new GovernedSqlQueryRequest(
            sql,
            Parameters: new Dictionary<string, object?> { [reservedName] = "test" },
            Transport: "WebSql");

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            pipeline.Service.ExecuteQueryBufferedAsync(request, User(AliceSid), Tenant));

        // Also verify SQL containing __param_ directly before normalization
        var rawParamSql = "SELECT o.id, c.name FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id WHERE o.id = __param_injected";
        var rawRequest = new GovernedSqlQueryRequest(rawParamSql, Transport: "WebSql");
        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            pipeline.Service.ExecuteQueryBufferedAsync(rawRequest, User(AliceSid), Tenant));
    }

    [Theory]
    [InlineData("' OR '1'='1")]
    [InlineData("'; DROP TABLE orders; --")]
    [InlineData("/* injected comment */ 1")]
    public async Task T_SEC_DATA131_09_SqlInjectionPayloads_TreatedAsLiterals(string injectionPayload)
    {
        var (pipeline, _, _, _, _) = CreatePipeline();
        var sql = "SELECT o.id, c.name FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id WHERE o.id = @injected";
        var request = new GovernedSqlQueryRequest(
            sql,
            Parameters: new Dictionary<string, object?> { ["injected"] = injectionPayload },
            Transport: "WebSql");

        var result = await pipeline.Service.ExecuteQueryBufferedAsync(request, User(AliceSid), Tenant);
        // Literal comparison: none of the order IDs equal the injection string, returns 0 rows safely
        result.Rows.Count.ShouldBe(0);
    }

    [Fact]
    public async Task T_SEC_DATA131_10_MissingParameter_ParityWithSingleSource()
    {
        var (pipeline, _, _, _, _) = CreatePipeline();
        var sql = "SELECT o.id, c.name FROM crm.public.orders o JOIN billing.public.customers c ON o.customer_id = c.id WHERE o.id = @unsupplied";
        var request = new GovernedSqlQueryRequest(
            sql,
            Parameters: new Dictionary<string, object?>(), // Unsupplied!
            Transport: "WebSql");

        // Fails closed - never silently executes with NULL
        await Should.ThrowAsync<Exception>(() =>
            pipeline.Service.ExecuteQueryBufferedAsync(request, User(AliceSid), Tenant));
    }

    [Fact]
    public async Task T_SEC_DATA131_11_MaskedColumn_ParameterizedPredicate_NeverPushed()
    {
        var ordersMeta = SqlTable("crm", "public", "orders", "id", "secret_code");
        var ast = FastSqlEngine.Default.Parse("SELECT * FROM crm.public.orders WHERE secret_code = __param_code".AsMemory()).Tree;
        var statement = new TrinoSqlEngine.Ast.Builder.SqlAstBuilder().BuildStatement(ast);
        var target = new TableAccessTarget("crm", "public", "orders", null, "crm.public.orders");

        var contract = FederatedParameterContract.Create(new Dictionary<string, object?> { ["code"] = "secret123" });

        // Decision marks secret_code as Mask
        var decision = TableAccessDecision.Allowed(
            ordersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel> { ["secret_code"] = ColumnAccessLevel.Mask },
            hasUnconstrainedColumnAllow: false);

        var pushed = PushedClauseBuilder.BuildPushdown(
            statement,
            target,
            ordersMeta,
            decision,
            contract,
            out _);

        // SEC-FED-19: Parameterized predicates on masked/HMAC/denied columns MUST NEVER be pushed
        pushed.ShouldBeNull();
    }
}
