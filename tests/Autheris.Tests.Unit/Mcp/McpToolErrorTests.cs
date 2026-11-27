namespace Autheris.Tests.Unit.Mcp;

using System.Text.Json;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Sql.Errors;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>One producer, one envelope: MCP tool errors carry a fixed code and message and nothing of the request; the guardrail treats them as an error result.</summary>
[Trait("Category", "Security")]
public sealed class McpToolErrorTests
{
    private static JsonElement ErrorOf(string json) => McpErrorEnvelopeAssert.Error(json);

    private static string[] Members(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void Serialize_Writes_Only_The_Executor_Shape()
    {
        var json = McpToolError.Serialize("INVALID_PARAMS", "Argument 'metric' is required.");

        Members(ErrorOf(json)).ShouldBe(["code", "message"]);
        ErrorOf(json).GetProperty("code").GetString().ShouldBe("INVALID_PARAMS");
        AiDataGuardrailService.TryGetExecutorError(json, out var code).ShouldBeTrue();
        code.ShouldBe("INVALID_PARAMS");
    }

    [Fact]
    public void Serialize_NeverWritesTenantIdToolOrIsError()
    {
        var json = McpToolError.Serialize(new McpToolErrorBody("EXECUTION_FAILED", "Tool execution failed.") { Hint = "h" });

        json.ShouldNotContain("tenantId");
        json.ShouldNotContain("\"tool\"");
        json.ShouldNotContain("isError");
        using var doc = JsonDocument.Parse(json);
        Members(doc.RootElement).ShouldBe(["error"]);
    }

    [Fact]
    public void Serialize_Body_WritesOptionalFieldsInFixedOrder_OmitsNulls()
    {
        using var graphQl = JsonDocument.Parse("""{"errors":[{"message":"m"}]}""");
        var json = McpToolError.Serialize(new McpToolErrorBody("SQL_LIMIT_EXCEEDED", "m")
        {
            GraphQL = graphQl.RootElement, RetryAfterSeconds = 4, MaxAllowedCost = 9, EstimatedCost = 10, Limit = "Rows", Column = 3, Line = 2, Hint = "h"
        });

        Members(ErrorOf(json)).ShouldBe(["code", "message", "hint", "line", "column", "limit", "estimatedCost", "maxAllowedCost", "retryAfterSeconds", "graphQL"]);
        Members(ErrorOf(McpToolError.Serialize(new McpToolErrorBody("EXECUTION_FAILED", "m")))).ShouldBe(["code", "message"]);
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("HAS SPACE")]
    [InlineData("")]
    [InlineData("X")]
    public void Ungoverned_Code_Falls_Back_To_ERROR(string code)
    {
        McpErrorEnvelopeAssert.Code(McpToolError.Serialize(code, "m")).ShouldBe("ERROR");
    }

    [Fact]
    public void Message_Is_Sanitized_And_Capped()
    {
        var message = McpErrorEnvelopeAssert.Message(McpToolError.Serialize("NOT_FOUND", "a\r\nb" + new string('z', 5000)));
        message.ShouldNotContain("\n");
        message.Length.ShouldBeLessThanOrEqualTo(McpToolError.MaxMessageLength);
    }

    [Fact]
    public void Fail_UsesFixedMessage_AndSanitizesText()
    {
        McpErrorEnvelopeAssert.Message(McpToolError.Fail(McpErrorCodes.DatasetNotFound).ContentJson).ShouldBe("Dataset not found or not visible.");
        var custom = McpToolError.Fail(McpErrorCodes.InvalidParams, "bad\r\n" + new string('x', 1000));
        custom.IsSuccess.ShouldBeFalse();
        custom.ErrorMessage.ShouldBe(custom.ContentJson);
        McpErrorEnvelopeAssert.Message(custom.ContentJson).Length.ShouldBeLessThanOrEqualTo(McpToolError.MaxMessageLength);
        McpErrorEnvelopeAssert.Message(custom.ContentJson).ShouldNotContain("\n");
    }

    [Fact]
    public void FromSql_SyntaxError_CarriesLineAndColumn()
    {
        var body = McpToolError.FromSql(null, new SqlErrorMapping(400, GovernedSqlErrorCodes.SyntaxError, "fixed", Line: 3, Column: 7));

        body.Line.ShouldBe(3);
        body.Column.ShouldBe(7);
        var error = ErrorOf(McpToolError.Serialize(body));
        error.GetProperty("line").GetInt32().ShouldBe(3);
        error.GetProperty("column").GetInt32().ShouldBe(7);
    }

    [Fact]
    public void FromSql_CostExceeded_CarriesEstimatedAndMaxAllowedCost()
    {
        var body = McpToolError.FromSql(new SqlCostExceededException(50, 20), new SqlErrorMapping(400, GovernedSqlErrorCodes.QueryCostExceeded, "fixed"));

        body.EstimatedCost.ShouldBe(50);
        body.MaxAllowedCost.ShouldBe(20);
    }

    [Fact]
    public void FromSql_QuotaAndWriteBudget_CarryRetryAfterSeconds()
    {
        McpToolError.FromSql(new SqlQuotaExhaustedException(11), new SqlErrorMapping(429, GovernedSqlErrorCodes.RateLimitExceeded, "fixed")).RetryAfterSeconds.ShouldBe(11);
        McpToolError.FromSql(new WriteBudgetExhaustedException("r", 13), new SqlErrorMapping(429, GovernedSqlErrorCodes.WriteBudgetExhausted, "fixed")).RetryAfterSeconds.ShouldBe(13);
    }

    [Fact]
    public void FromSql_Status503_CarriesRetryAfterFive()
    {
        McpToolError.FromSql(new InvalidOperationException(), new SqlErrorMapping(503, GovernedSqlErrorCodes.ServiceUnavailable, "fixed")).RetryAfterSeconds.ShouldBe(5);
        McpToolError.FromSql(new InvalidOperationException(), new SqlErrorMapping(400, GovernedSqlErrorCodes.SyntaxError, "fixed")).RetryAfterSeconds.ShouldBeNull();
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var foreign = """{"type":"urn:x","status":400,"code":"INVALID_QUERY","title":"upstream title"}""";
        var once = McpToolError.Normalize(foreign);

        McpToolError.Normalize(once).ShouldBe(once);
        McpToolError.Normalize(McpToolError.Normalize(once)).ShouldBe(once);
        McpErrorEnvelopeAssert.Code(once).ShouldBe("INVALID_QUERY");
    }

    [Fact]
    public void Normalize_ProblemDetails_KeepsCodeTitleAndAllowedExtensionsOnly()
    {
        var json = McpToolError.Normalize("""
            {"type":"urn:autheris:error:SQL_SYNTAX_ERROR","title":"SELECT secret FROM t","status":400,"detail":"d","code":"SQL_SYNTAX_ERROR","line":2,"column":9,"traceId":"t","password":"p"}
            """);

        var error = ErrorOf(json);
        Members(error).ShouldBe(["code", "message", "line", "column"]);
        error.GetProperty("message").GetString().ShouldBe(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.SyntaxError));
        json.ShouldNotContain("secret");
    }

    [Fact]
    public void Normalize_JsonRpcNumericCode_BecomesExecutionFailed()
    {
        var json = McpToolError.Normalize("""{"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"Invalid Request for table t"}}""");

        McpErrorEnvelopeAssert.Code(json).ShouldBe("EXECUTION_FAILED");
        json.ShouldNotContain("table t");
    }

    [Theory]
    [InlineData("plain failure text for table secret")]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    public void Normalize_FreeTextOrInvalidJson_BecomesExecutionFailed(string? input)
    {
        var json = McpToolError.Normalize(input);

        McpErrorEnvelopeAssert.Code(json).ShouldBe("EXECUTION_FAILED");
        McpErrorEnvelopeAssert.Message(json).ShouldBe(McpErrorCodes.FixedMessage(McpErrorCodes.ExecutionFailed));
    }

    [Fact]
    public void Normalize_DropsUnknownMembers()
    {
        var json = McpToolError.Normalize("""{"error":{"code":"QUERY_TIMEOUT","message":"m","extra":"x","stack":"s"},"tenantId":"t","isError":true}""");

        Members(ErrorOf(json)).ShouldBe(["code", "message"]);
        json.ShouldNotContain("extra");
    }

    [Fact]
    public void Normalize_RelayedBodyWithOwnShape_NeverCopiesUpstreamMessage()
    {
        // An upstream body that merely has the envelope shape: its message is attacker-chosen text.
        var json = McpToolError.Normalize("""{"error":{"code":"DATASET_NOT_FOUND","message":"Dataset secret.salary does not exist for tenant acme"}}""");

        McpErrorEnvelopeAssert.Code(json).ShouldBe("DATASET_NOT_FOUND");
        McpErrorEnvelopeAssert.Message(json).ShouldBe("Dataset not found or not visible.");
        json.ShouldNotContain("salary");
    }

    [Fact]
    public void Normalize_UnknownCode_BecomesExecutionFailed()
    {
        var json = McpToolError.Normalize("""{"error":{"code":"CUSTOM_UPSTREAM_CODE","message":"m","hint":"leak","line":4}}""");

        McpErrorEnvelopeAssert.Code(json).ShouldBe("EXECUTION_FAILED");
        Members(ErrorOf(json)).ShouldBe(["code", "message"]);
    }

    [Fact]
    public void Normalize_OversizedOrMalformed_NeverThrows_FailsClosed()
    {
        var oversized = "{\"error\":{\"code\":\"QUERY_TIMEOUT\",\"message\":\"m\",\"pad\":\"" + new string('x', McpToolError.MaxNormalizeInputLength) + "\"}}";
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);

        foreach (var input in new[] { oversized, deep, "\u0000\u0001", "{\"error\":" })
        {
            string? output = null;
            Should.NotThrow(() => output = McpToolError.Normalize(input));
            McpErrorEnvelopeAssert.Code(output).ShouldBe("EXECUTION_FAILED");
        }
    }

    [Fact]
    public void Normalize_HintOnlyForCatalogueCodes_AndBounded()
    {
        var longHint = new string('h', 2000);
        var known = McpToolError.Normalize("{\"error\":{\"code\":\"TOKEN_BUDGET_EXCEEDED\",\"message\":\"m\",\"hint\":\"" + longHint + "\"}}");
        var unknown = McpToolError.Normalize("{\"error\":{\"code\":\"NOT_IN_CATALOGUE\",\"message\":\"m\",\"hint\":\"" + longHint + "\"}}");

        ErrorOf(known).GetProperty("hint").GetString()!.Length.ShouldBeLessThanOrEqualTo(McpToolError.MaxHintLength);
        ErrorOf(unknown).TryGetProperty("hint", out _).ShouldBeFalse();
        var own = McpToolError.Serialize(new McpToolErrorBody("TOKEN_BUDGET_EXCEEDED", "m") { Hint = longHint });
        ErrorOf(own).GetProperty("hint").GetString()!.Length.ShouldBeLessThanOrEqualTo(McpToolError.MaxHintLength);
    }

    [Fact]
    public void FailAdmin_OnlyAllowlistedTextsPass_OtherTextIsReplacedByTheFixedMessage()
    {
        McpToolError.FailAdmin("Invalid or already consumed TOTP verification code.").ErrorMessage!.ShouldContain("TOTP verification code");
        var leaked = McpToolError.FailAdmin("Plan 'plan_secret' has a datasource password=hunter2.");

        McpErrorEnvelopeAssert.Code(leaked).ShouldBe("ADMIN_OPERATION_REJECTED");
        McpErrorEnvelopeAssert.Message(leaked.ContentJson).ShouldBe(McpErrorCodes.FixedMessage(McpErrorCodes.AdminOperationRejected));
        leaked.ContentJson.ShouldNotContain("hunter2");
    }
}
