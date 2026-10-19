namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Connectors;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
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

public sealed class CrossChannelIntegrationFixesTests
{
    // =========================================================================
    // WP-243-1: ReBAC Cycle Detection in Diamond DAG Hierarchies & MCP Error
    // =========================================================================

    [Fact]
    public async Task Rebac_DiamondInheritance_CanQueryAllowed_WithoutCyclicWarning()
    {
        // Arrange
        var options = Options.Create(new GatewayOptions
        {
            Rebac = new RebacOptions
            {
                Enabled = true,
                MaxTraversalDepth = 10,
                CacheTtlSeconds = 10
            }
        });

        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        // Standard hierarchy:
        // "can_query" is inherited by "viewer", "editor", "owner"
        // "viewer" is inherited by "editor" and "owner"
        // "editor" is inherited by "owner"
        // Direct tuple: user:alice is "owner" of "table:sales.analytics.orders"
        await store.AddTupleAsync(new RebacTuple("tenant-1", "user:alice", "owner", "table:sales.analytics.orders"));

        // Act
        var result = await evaluator.CheckAsync(new RebacCheckRequest("tenant-1", "user:alice", "can_query", "table:sales.analytics.orders"));

        // Assert
        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Rebac_TrueCycle_DetectedAndDenied_FailsClosed()
    {
        // Arrange
        var options = Options.Create(new GatewayOptions
        {
            Rebac = new RebacOptions
            {
                Enabled = true,
                MaxTraversalDepth = 10,
                CacheTtlSeconds = 10
            }
        });

        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        // Define a mutually recursive cycle: roleA inherits roleB, and roleB inherits roleA
        evaluator.RegisterInheritance("roleA", "roleB");
        evaluator.RegisterInheritance("roleB", "roleA");

        // Act: bob has neither roleA nor roleB tuple; evaluation should traverse and detect cycle without infinite recursion
        var result = await evaluator.CheckAsync(new RebacCheckRequest("tenant-1", "user:bob", "roleA", "table:sales.analytics.orders"));

        // Assert
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task McpToolExecutionHandler_QueryDataset_UnauthorizedAccessException_ReturnsCleanErrorPayload()
    {
        // Arrange
        var sqlExecutionService = Substitute.For<IGovernedSqlExecutionService>();
        var dataQueryService = Substitute.For<IGovernedDataQueryService>();
        var catalogDiscoveryService = Substitute.For<ICatalogDiscoveryService>();
        var apiDispatcherService = Substitute.For<IApiDispatcherService>();
        var lineageGraphStore = Substitute.For<ILineageGraphStore>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();

        dataQueryService.ExecuteQueryAsync(
            Arg.Any<DatasetQueryRequest>(),
            Arg.Any<RequestContext>(),
            Arg.Any<CancellationToken>())
            .Returns<Task<DatasetQueryEnvelope>>(_ => throw new UnauthorizedAccessException("Subject lacks permission to access dataset 'sales.orders'."));

        var handler = new McpToolExecutionHandler(
            sqlExecutionService,
            dataQueryService,
            catalogDiscoveryService,
            apiDispatcherService,
            lineageGraphStore,
            metadataRepo);

        var toolDef = new McpToolDefinition(
            Name: McpDatasetTools.QueryDataset,
            Description: "Query dataset",
            InputJsonSchema: "{}",
            TargetGraphQLOperation: "");

        var session = new McpSessionContext("sess-1", "user-principal", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        // Act
        var resultJson = await handler.ExecuteToolAsync(
            toolDef,
            """{"dataset":"sales.public.orders"}""",
            session,
            CancellationToken.None);

        // Assert
        resultJson.ShouldContain("Unauthorized");
        resultJson.ShouldContain("Subject lacks permission to access dataset 'sales.orders'.");
    }

    // =========================================================================
    // WP-243-2: OData & Connector Reader $select Column Scoping
    // =========================================================================

    [Fact]
    public void GovernedConnectorReader_ProjectRow_WithRequestedColumns_ProjectsOnlyRequestedColumns()
    {
        // Arrange
        var meta = new TableMetadata
        {
            Table = new Table { TableName = "orders", SourceName = "sales", SchemaName = "public" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "bigint" },
                new TableColumn { ColumnName = "customer_name", DataType = "string" },
                new TableColumn { ColumnName = "email", DataType = "string" },
                new TableColumn { ColumnName = "total_amount", DataType = "decimal" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        var rawRow = new Dictionary<string, object?>
        {
            ["id"] = 42L,
            ["customer_name"] = "Alice Corp",
            ["email"] = "alice@example.com",
            ["total_amount"] = 199.99m
        };

        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var masking = Substitute.For<IColumnMaskingProvider>();
        var policy = new GovernedRowPolicy(masking, HmacKeyId: null, RequestedColumns: new HashSet<string>(["id", "customer_name"], StringComparer.OrdinalIgnoreCase));

        // Act
        var projected = GovernedConnectorReader.ProjectRow(rawRow, meta, decision, "tenant-1", policy, alreadyMasked: false);

        // Assert
        projected.ContainsKey("id").ShouldBeTrue();
        projected.ContainsKey("customer_name").ShouldBeTrue();
        projected.ContainsKey("email").ShouldBeFalse();
        projected.ContainsKey("total_amount").ShouldBeFalse();
        projected["id"].ShouldBe(42L);
        projected["customer_name"].ShouldBe("Alice Corp");
    }

    [Fact]
    public void GovernedConnectorReader_ProjectRow_WithRequestedColumns_StripsDeniedColumnsEvenIfRequested()
    {
        // Arrange
        var meta = new TableMetadata
        {
            Table = new Table { TableName = "orders", SourceName = "sales", SchemaName = "public" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "bigint" },
                new TableColumn { ColumnName = "salary", DataType = "decimal" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        var rawRow = new Dictionary<string, object?>
        {
            ["id"] = 1L,
            ["salary"] = 100000m
        };

        var decision = TableAccessDecision.Allowed(
            meta.Identifier,
            new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["salary"] = ColumnAccessLevel.Deny },
            hasUnconstrainedColumnAllow: false);

        var masking = Substitute.For<IColumnMaskingProvider>();
        var policy = new GovernedRowPolicy(masking, HmacKeyId: null, RequestedColumns: new HashSet<string>(["id", "salary"], StringComparer.OrdinalIgnoreCase));

        // Act
        var projected = GovernedConnectorReader.ProjectRow(rawRow, meta, decision, "tenant-1", policy, alreadyMasked: false);

        // Assert
        projected.ContainsKey("id").ShouldBeTrue();
        projected.ContainsKey("salary").ShouldBeFalse();
    }

    // =========================================================================
    // WP-243-3: Clamped Consent Expiry Zero-Trust Diagnostic
    // =========================================================================

    [Fact]
    public void ConsentResolutionService_ResolveAccess_ClampedExpiredConsent_ReturnsClearDiagnosticDenial()
    {
        // Arrange
        var resolver = new ConsentResolutionService();
        var table = new TableIdentifier("sales", "public", "orders");
        var userSid = new Sid("user-alice");
        var now = DateTimeOffset.UtcNow;

        var clampedConsent = new Consent
        {
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = now.AddDays(-200),
            ValidTo = now.AddDays(20) // ValidTo is in future, but clamped to 180 days so EffectiveValidTo was 20 days ago
        };

        // Act
        var decision = resolver.ResolveAccess(userSid, new HashSet<Sid>(), new HashSet<string>(), table, [clampedConsent]);

        // Assert
        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldNotBeEmpty();
        decision.DeniedReasons[0].ShouldContain("Zero Trust: No active ALLOW consent granted to subject (consent expired on");
        decision.DeniedReasons[0].ShouldContain("due to maximum duration clamp).");
        decision.DeniedReasons[0].ShouldContain(clampedConsent.EffectiveValidTo.ToString("yyyy-MM-dd"));
    }

    // =========================================================================
    // WP-243-4: MCP SQL Syntax Error & Tableless Self-Access Guardrail
    // =========================================================================

    [Fact]
    public async Task AiDataGuardrailService_QuerySql_WithSyntaxError_ReturnsSqlSyntaxErrorMessage()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var querySqlTool = new McpToolDefinition(
            Name: McpDatasetTools.QuerySql,
            Description: "Execute SQL query",
            InputJsonSchema: """{"type":"object"}""",
            TargetGraphQLOperation: "");
        registry.RegisterTool(querySqlTool);

        var options = Options.Create(new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true }
        });
        var logger = NullLogger<AiDataGuardrailService>.Instance;
        var auditLog = global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance;

        var guardrail = new AiDataGuardrailService(registry, options, logger, auditLog);

        var session = new McpSessionContext("sess-1", "user-principal", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest(McpDatasetTools.QuerySql, """{"query":"SELECT FROM WHERE"}""");

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldStartWith("SQL_SYNTAX_ERROR:");
    }

    [Fact]
    public void McpDataTargetResolver_TryResolveSqlTables_WithInvalidSql_ReturnsSyntaxError()
    {
        // Act
        var (tables, syntaxErr) = McpDataTargetResolver.TryResolveSqlTables("SELECT FROM WHERE", null, "default");

        // Assert
        tables.ShouldBeNull();
        syntaxErr.ShouldNotBeNull();
    }

    [Fact]
    public async Task AiDataGuardrailService_DescribeMyAccess_WithoutDataset_DoesNotDenyForUnresolvableTable()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var describeTool = new McpToolDefinition(
            Name: McpDatasetTools.DescribeMyAccess,
            Description: "Describe caller access",
            InputJsonSchema: """{"type":"object"}""",
            TargetGraphQLOperation: McpDatasetTools.DescribeMyAccess);
        registry.RegisterTool(describeTool);

        var options = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_mcp_auth = true },
            Mcp = new McpOptions { Enabled = true }
        });
        var logger = NullLogger<AiDataGuardrailService>.Instance;
        var auditLog = global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance;

        var queryExecutor = Substitute.For<IMcpQueryExecutor>();
        queryExecutor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{}"));

        var guardrail = new AiDataGuardrailService(registry, options, logger, auditLog, queryExecutor: queryExecutor);

        var session = new McpSessionContext("sess-1", "user-principal", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest(McpDatasetTools.DescribeMyAccess, "{}");

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        // Assert: It should execute through to query executor and NOT fail with "target data table could not be resolved (fail-closed)"
        result.ErrorMessage?.ShouldNotContain("target data table could not be resolved (fail-closed)");
    }
}
