namespace Autheris.Tests.Unit.Mcp;

using System.Reflection;
using Autheris.Api.Errors;
using Autheris.Application.Mcp.Services;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

public sealed class McpErrorCodesTests
{
    private static IEnumerable<string> Codes() =>
        typeof(McpErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void EveryCode_HasAFixedMessage()
    {
        foreach (var code in Codes())
        {
            McpErrorCodes.IsCatalogueCode(code).ShouldBeTrue(code);
            var message = McpErrorCodes.FixedMessage(code);
            message.ShouldNotBeNullOrWhiteSpace(code);
            if (code != McpErrorCodes.ExecutionFailed)
            {
                message.ShouldNotBe(McpErrorCodes.DefaultMessage, code);
            }
        }

        McpErrorCodes.FixedMessage("NOT_A_CODE").ShouldBe(McpErrorCodes.DefaultMessage);
        McpErrorCodes.FixedMessage(GovernedSqlErrorCodes.SyntaxError).ShouldBe(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.SyntaxError));
        McpErrorCodes.IsCatalogueCode(GovernedSqlErrorCodes.PolicyDenied).ShouldBeTrue();
        McpErrorCodes.IsCatalogueCode("NOT_A_CODE").ShouldBeFalse();
    }

    [Fact]
    public void HttpTwinCodes_EqualApiErrorCodes()
    {
        McpErrorCodes.InvalidQuery.ShouldBe(ApiErrorCodes.InvalidQuery);
        McpErrorCodes.MetricNotFound.ShouldBe(ApiErrorCodes.MetricNotFound);
        McpErrorCodes.ResourceNotFound.ShouldBe(ApiErrorCodes.ResourceNotFound);
        McpErrorCodes.RequiredFilterColumnMissing.ShouldBe(ApiErrorCodes.RequiredFilterColumnMissing);
        McpErrorCodes.RateLimitExceeded.ShouldBe(ApiErrorCodes.RateLimitExceeded);
    }

    [Theory]
    [InlineData("query_sql", "SQL_POLICY_DENIED")]
    [InlineData("query_metrics", "METRIC_NOT_FOUND")]
    [InlineData("call_procedure", "RESOURCE_NOT_FOUND")]
    [InlineData("describe_dataset", "DATASET_NOT_FOUND")]
    [InlineData("query_dataset", "DATASET_NOT_FOUND")]
    [InlineData("sample_rows", "DATASET_NOT_FOUND")]
    [InlineData("get_table_schema", "DATASET_NOT_FOUND")]
    [InlineData("query_graphql", "DATASET_NOT_FOUND")]
    [InlineData("invoke_api", "DATASET_NOT_FOUND")]
    [InlineData("simulate_query", "DATASET_NOT_FOUND")]
    [InlineData("list_orders", "TOOL_NOT_AVAILABLE")]
    [InlineData("anything_else", "TOOL_NOT_AVAILABLE")]
    public void TargetDeniedCode_PerTool(string tool, string expected)
    {
        McpErrorCodes.TargetDeniedCode(tool).ShouldBe(expected);
        McpErrorCodes.TargetDeniedCode(tool.ToUpperInvariant()).ShouldBe(expected);
    }
}
