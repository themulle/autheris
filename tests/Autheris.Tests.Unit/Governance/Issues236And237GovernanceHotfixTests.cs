using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using RequiredFilterColumnMissingException = TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

public sealed class Issues236And237GovernanceHotfixTests
{
    // =========================================================================
    // Issue #236: Unified Required Filter Column Groups across read paths
    // =========================================================================

    private static TableMetadata CreateTableWithRequiredFilterGroups()
    {
        return new TableMetadata
        {
            Table = new Table
            {
                TableName = "audit_events",
                SourceName = "security",
                SchemaName = "public",
                RequiredFilterColumnGroups = ImmutableArray.Create(
                    ImmutableArray.Create("customer_id"),
                    ImmutableArray.Create("region", "event_date"))
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "bigint" },
                new TableColumn { ColumnName = "customer_id", DataType = "string" },
                new TableColumn { ColumnName = "region", DataType = "string" },
                new TableColumn { ColumnName = "event_date", DataType = "date" },
                new TableColumn { ColumnName = "action", DataType = "string" }
            ],
            PrimaryKeyColumns = ["id"]
        };
    }

    [Fact]
    public void Issue236_GovernedTableReadPlanner_Plan_SatisfiedBySingleGroup_Succeeds()
    {
        var metadata = CreateTableWithRequiredFilterGroups();
        var planner = new GovernedTableReadPlanner();
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        // Group 1 satisfied ("customer_id")
        var filter1 = TableFilterClause.FromTrinoText("customer_id = 'corp'", ["customer_id"]);
        var intent1 = new TableReadIntent(metadata.Identifier, ["id"], filter1, null, 10, 0, "Rest");
        var plan1 = planner.Plan(intent1, metadata, decision);
        plan1.ShouldNotBeNull();
        plan1.Sql.ShouldContain("FROM");

        // Group 2 satisfied ("region" AND "event_date")
        var filter2 = TableFilterClause.FromTrinoText("region = 'us-east' AND event_date = '2026-10-19'", ["region", "event_date"]);
        var intent2 = new TableReadIntent(metadata.Identifier, ["id"], filter2, null, 10, 0, "Rest");
        var plan2 = planner.Plan(intent2, metadata, decision);
        plan2.ShouldNotBeNull();
        plan2.Sql.ShouldContain("FROM");
    }

    [Fact]
    public void Issue236_GovernedTableReadPlanner_Plan_UnsatisfiedGroup_ThrowsRequiredFilterColumnMissingException()
    {
        var metadata = CreateTableWithRequiredFilterGroups();
        var planner = new GovernedTableReadPlanner();
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        // Only "region" provided, but "event_date" is missing
        var filter = TableFilterClause.FromTrinoText("region = 'us-east'", ["region"]);
        var intent = new TableReadIntent(metadata.Identifier, ["id"], filter, null, 10, 0, "Rest");

        Should.Throw<RequiredFilterColumnMissingException>(() => planner.Plan(intent, metadata, decision));

        // No filter at all
        var intentNoFilter = new TableReadIntent(metadata.Identifier, ["id"], null, null, 10, 0, "Rest");
        Should.Throw<RequiredFilterColumnMissingException>(() => planner.Plan(intentNoFilter, metadata, decision));
    }

    [Fact]
    public void Issue236_GovernedTableReadPlanner_PlanAggregate_SatisfiedByGroup_Succeeds()
    {
        var metadata = CreateTableWithRequiredFilterGroups();
        var planner = new GovernedTableReadPlanner();
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var filter = TableFilterClause.FromTrinoText("customer_id = 'corp'", ["customer_id"]);
        var intent = new TableAggregateIntent(
            metadata.Identifier,
            GroupBy: ["region"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, "id", "cnt")],
            Filter: filter,
            OrderBy: null,
            Limit: 10,
            Transport: "Rest");

        var plan = planner.PlanAggregate(intent, metadata, decision);
        plan.ShouldNotBeNull();
        plan.Sql.ShouldContain("COUNT");
    }

    [Fact]
    public void Issue236_GovernedTableReadPlanner_PlanAggregate_Unsatisfied_ThrowsRequiredFilterColumnMissingException()
    {
        var metadata = CreateTableWithRequiredFilterGroups();
        var planner = new GovernedTableReadPlanner();
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var intent = new TableAggregateIntent(
            metadata.Identifier,
            GroupBy: ["region"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, "id", "cnt")],
            Filter: null,
            OrderBy: null,
            Limit: 10,
            Transport: "Rest");

        Should.Throw<RequiredFilterColumnMissingException>(() => planner.PlanAggregate(intent, metadata, decision));
    }

    [Fact]
    public void Issue236_GovernedTableReadPlanner_PlanKeysetStream_RequiredFilterGroups_Enforced()
    {
        var metadata = CreateTableWithRequiredFilterGroups();
        var planner = new GovernedTableReadPlanner();
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        // Satisfied with customer_id filter
        var satisfiedRequest = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(Field: "customer_id", Op: "eq", Value: JsonDocument.Parse("\"corp\"").RootElement),
            Limit: 100);
        var plan = planner.PlanKeysetStream(satisfiedRequest, metadata, decision);
        plan.ShouldNotBeNull();

        // Unsatisfied with empty filter
        var unsatisfiedRequest = new BulkTableStreamRequest(Filter: null, Limit: 100);
        Should.Throw<Autheris.Domain.Model.RequiredFilterColumnMissingException>(() => planner.PlanKeysetStream(unsatisfiedRequest, metadata, decision));
    }

    // =========================================================================
    // Issue #237 WP-237-2: Keyset Cursor HMAC Cryptographic Context Binding
    // =========================================================================

    [Fact]
    public void Issue237_KeysetCursorEncoder_ContextBinding_SuccessAndTamperDetection()
    {
        var metadata = CreateTableWithRequiredFilterGroups();
        var encoder = new KeysetCursorEncoder("super-secret-hmac-key-for-test-32bytes!");
        var context = new KeysetCursorContext(
            TenantId: "tenant-gold",
            DataSource: "security",
            TableName: "audit_events",
            Direction: "Ascending",
            Columns: ["id"],
            Caller: "data-pipeline-app");

        var pkValues = new Dictionary<string, object?>
        {
            ["id"] = 42L
        };

        var cursor = encoder.EncodeCursor(pkValues, context);
        cursor.ShouldNotBeNullOrWhiteSpace();

        // Decoding with identical context succeeds
        var decoded = encoder.DecodeCursor(cursor, metadata, context);
        decoded.ShouldNotBeNull();
        Convert.ToInt64(decoded["id"]).ShouldBe(42L);

        // Tampering TenantId fails closed
        var wrongTenant = context with { TenantId = "tenant-silver" };
        var exTenant = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(cursor, metadata, wrongTenant));
        exTenant.Message.ShouldContain("INVALID_CURSOR");

        // Tampering TableName fails closed
        var wrongTable = context with { TableName = "orders" };
        var exTable = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(cursor, metadata, wrongTable));
        exTable.Message.ShouldContain("INVALID_CURSOR");

        // Tampering DataSource fails closed
        var wrongSource = context with { DataSource = "finance" };
        var exSource = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(cursor, metadata, wrongSource));
        exSource.Message.ShouldContain("INVALID_CURSOR");

        // Tampering Direction fails closed
        var wrongDir = context with { Direction = "Descending" };
        var exDir = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(cursor, metadata, wrongDir));
        exDir.Message.ShouldContain("INVALID_CURSOR");

        // Tampering Caller fails closed
        var wrongCaller = context with { Caller = "other-app" };
        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(cursor, metadata, wrongCaller));

        // Tampering Schema or request shape fails closed
        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(cursor, metadata, context with { Schema = "archive" }));
        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(cursor, metadata, context with { RequestHash = "different" }));

        // Unbound (context-less) cursors are no longer issued or accepted (no bare-signature fallback)
        Should.Throw<ArgumentNullException>(() => encoder.EncodeCursor(pkValues, null!));
        Should.Throw<ArgumentNullException>(() => encoder.DecodeCursor(cursor, metadata, null!));
    }

    // =========================================================================
    // Issue #237 WP-237-3: Nested Cost Settlement Precision
    // =========================================================================

    [Fact]
    public void Issue237_CostAndQuotaMiddleware_CountResultRows_RecursivelyCountsNestedRows()
    {
        // 10 parent customers, each with 100 child orders = 10 + (10 * 100) = 1,010 total rows
        var customers = new List<Dictionary<string, object?>>();
        for (int i = 0; i < 10; i++)
        {
            var orders = new List<Dictionary<string, object?>>();
            for (int j = 0; j < 100; j++)
            {
                orders.Add(new Dictionary<string, object?>
                {
                    ["orderId"] = j,
                    ["amount"] = 49.99
                });
            }

            customers.Add(new Dictionary<string, object?>
            {
                ["customerId"] = i,
                ["name"] = $"Customer_{i}",
                ["orders"] = orders
            });
        }

        var resultData = new Dictionary<string, object?>
        {
            ["customers"] = customers
        };

        var rowCount = CostAndQuotaMiddleware.CountResultRows(resultData);
        rowCount.ShouldBe(1010);
    }

    // =========================================================================
    // Issue #237 WP-237-4: Query Analysis rowsPerRequest, requests & secondsAtCurrentBucket
    // =========================================================================

    [Fact]
    public void Issue237_QueryCostEstimate_And_ValidationReport_ExposePagingBounds()
    {
        var estimate = new QueryCostEstimate(
            EstimatedCost: 5000,
            MaxAllowedCost: 1000,
            IsWithinCostLimit: false,
            RootFieldCosts: new Dictionary<string, int> { ["events"] = 5000 },
            EstimatedRows: 10000,
            RowsPerRequest: 1000,
            Requests: 10,
            SecondsAtCurrentBucket: 12.5);

        estimate.RowsPerRequest.ShouldBe(1000);
        estimate.Requests.ShouldBe(10);
        estimate.SecondsAtCurrentBucket.ShouldBe(12.5);

        var report = new QueryValidationReport(
            IsValid: true,
            Diagnostics: [],
            OutputColumns: [],
            HasRowFilters: true,
            HasMandatoryVirtualFilters: false,
            CostEstimate: estimate,
            DeprecatedFields: []);

        report.RowsPerRequest.ShouldBe(1000);
        report.Requests.ShouldBe(10);
        report.SecondsAtCurrentBucket.ShouldBe(12.5);
    }

    // =========================================================================
    // Issue #237 WP-237-5: Self-Service Disable & Error Formatting
    // =========================================================================

    [Fact]
    public void Issue237_EntraIdAuthOptions_SelfServiceEnabled_DefaultsToTrue()
    {
        var options = new EntraIdAuthOptions();
        options.SelfServiceEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Issue237_ReadOnlyOperationMiddleware_RejectsSelfService_WhenSelfServiceDisabled()
    {
        var options = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions
                {
                    SelfServiceEnabled = false,
                    ReadOnlySelfServiceScopes = ["data.access.request"]
                }
            }
        };

        var executor = await new ServiceCollection()
            .AddSingleton(Options.Create(options))
            .AddGraphQLServer()
            .AddQueryType<DummyQuery>()
            .AddMutationType<DummyMutation>()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseRequest<ReadOnlyOperationMiddleware>()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseOperationExecution()
            .BuildRequestExecutorAsync();

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-ro"),
            new Claim("scp", "Agent.Read data.access.request")
        ], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        var readOnlyPrincipal = new ClaimsPrincipal(identity);

        var request = OperationRequestBuilder.New()
            .SetDocument("mutation { requestTableAccess }")
            .AddGlobalState(nameof(ClaimsPrincipal), readOnlyPrincipal)
            .Build();

        var result = (OperationResult)await executor.ExecuteAsync(request);
        result.Errors.ShouldNotBeNull();
        result.Errors.Count.ShouldBeGreaterThan(0);
        result.Errors[0].Code.ShouldBe("READ_ONLY_TOKEN");
        result.Errors[0].Message.ShouldBe("This token only permits read access; mutations are rejected.");
    }

    [Fact]
    public void Issue237_EnsureReviewScope_ThrowsForbidden_WhenSelfServiceDisabled()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton(Options.Create(new GatewayOptions
            {
                Authentication = new AuthenticationOptions
                {
                    EntraId = new EntraIdAuthOptions
                    {
                        SelfServiceEnabled = false
                    }
                }
            }))
            .BuildServiceProvider();

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "approver-1"),
            new Claim("scp", "data.access.review")
        ], "Bearer"));

        var ex = Should.Throw<GraphQLException>(() => Mutation.EnsureReviewScope(accessor, principal));
        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        ex.Errors[0].Message.ShouldBe("Self-service access review is disabled.");
    }

    [Fact]
    public void Issue237_EnsureReviewScope_ThrowsForbidden_WhenAccessReviewScopesEmpty()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton(Options.Create(new GatewayOptions
            {
                Authentication = new AuthenticationOptions
                {
                    EntraId = new EntraIdAuthOptions
                    {
                        SelfServiceEnabled = true,
                        AccessReviewScopes = []
                    }
                }
            }))
            .BuildServiceProvider();

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "approver-1"),
            new Claim("scp", "data.access.review")
        ], "Bearer"));

        var ex = Should.Throw<GraphQLException>(() => Mutation.EnsureReviewScope(accessor, principal));
        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        ex.Errors[0].Message.ShouldBe("Self-service access review is disabled.");
    }

    [Fact]
    public void Issue237_EnsureReviewScope_FormatsErrorMessageDynamically()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton(Options.Create(new GatewayOptions
            {
                Authentication = new AuthenticationOptions
                {
                    EntraId = new EntraIdAuthOptions
                    {
                        SelfServiceEnabled = true,
                        AccessReviewScopes = ["review.security", "review.governance"]
                    }
                }
            }))
            .BuildServiceProvider();

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "approver-1"),
            new Claim("scp", "data.access.request") // missing review scopes
        ], "Bearer"));

        var ex = Should.Throw<GraphQLException>(() => Mutation.EnsureReviewScope(accessor, principal));
        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        ex.Errors[0].Message.ShouldBe("The 'review.security', 'review.governance' scope is required to review consent requests.");
    }

    public sealed class DummyQuery
    {
        public string Ping() => "pong";
    }

    public sealed class DummyMutation
    {
        public string RequestTableAccess() => "ok";
    }
}
