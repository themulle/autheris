namespace Autheris.Tests.Unit.Sql.Errors;

using System;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Issue #277 (SEC-277-04): the MCP <c>query_sql</c> surface refuses a keyword-less CTE self-reference before any table resolution.</summary>
public sealed class McpRecursiveCteGuardrailTests
{
    private static async Task<McpToolCallResult> RunAsync(string sql)
    {
        var registry = new McpToolRegistry();
        registry.RegisterTool(new McpToolDefinition(
            Name: McpDatasetTools.QuerySql,
            Description: "Execute SQL query",
            InputJsonSchema: """{"type":"object"}""",
            TargetGraphQLOperation: ""));
        var options = Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } });
        var guardrail = new AiDataGuardrailService(
            registry, options, NullLogger<AiDataGuardrailService>.Instance, global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance);
        var session = new McpSessionContext("sess-1", "user-principal", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var query = System.Text.Json.JsonSerializer.Serialize(new { query = sql });

        return await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(McpDatasetTools.QuerySql, query), session);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_277_04_KeywordlessSelfReference_SameResponseForExistingForbiddenAndUnknownTable()
    {
        var existing = await RunAsync("WITH orders AS (SELECT id FROM orders) SELECT id FROM orders");
        var unknown = await RunAsync("WITH ghost_table AS (SELECT id FROM ghost_table) SELECT id FROM ghost_table");

        var forbidden = await RunAsync("WITH payroll AS (SELECT id FROM payroll) SELECT id FROM payroll");

        forbidden.ErrorMessage.ShouldBe(existing.ErrorMessage);
        existing.IsSuccess.ShouldBeFalse();
        existing.ErrorMessage.ShouldStartWith("SQL_CONSTRUCT_NOT_SUPPORTED");
        unknown.ErrorMessage.ShouldBe(existing.ErrorMessage);
        existing.ErrorMessage.ShouldNotContain("orders", Case.Insensitive);
        existing.ErrorMessage.ShouldNotContain("ghost", Case.Insensitive);
    }

    [Fact]
    public void Resolver_RecursiveCte_DoesNotResolveTheCteNameAsTable()
    {
        var (tables, error) = McpDataTargetResolver.TryResolveSqlTables(
            "WITH RECURSIVE t(id) AS (SELECT id FROM orders UNION ALL SELECT id + 1 FROM t WHERE id < 3) SELECT id FROM t", null, "default");

        error.ShouldBeNull();
        tables.ShouldNotBeNull();
        tables.Count.ShouldBe(1);
        tables[0].TableName.ShouldBe("orders");
    }
}
