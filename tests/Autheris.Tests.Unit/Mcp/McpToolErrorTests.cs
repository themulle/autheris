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

        Members(ErrorOf(json)).ShouldBe(["code", "message", "detail"]);
        ErrorOf(json).GetProperty("code").GetString().ShouldBe("INVALID_PARAMS");
        McpErrorEnvelopeAssert.Message(json).ShouldBe(McpErrorCodes.FixedMessage(McpErrorCodes.InvalidParams));
        McpErrorEnvelopeAssert.Detail(json).ShouldBe("Argument 'metric' is required.");
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
        var kind = Enum.GetNames<TrinoSqlEngine.Ast.Capabilities.SqlLimitKind>()[0];
        var json = McpToolError.Serialize(new McpToolErrorBody("SQL_LIMIT_EXCEEDED", "m")
        {
            RetryAfterSeconds = 4, MaxAllowedCost = 9, EstimatedCost = 10, Limit = kind, Column = 3, Line = 2, Hint = "h"
        });

        Members(ErrorOf(json)).ShouldBe(["code", "message", "hint", "line", "column", "limit", "estimatedCost", "maxAllowedCost", "retryAfterSeconds"]);
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
        var detail = McpErrorEnvelopeAssert.Detail(McpToolError.Serialize("INVALID_PARAMS", "a\r\nb" + new string('z', 5000)))!;
        detail.ShouldNotContain("\n");
        detail.Length.ShouldBeLessThanOrEqualTo(McpToolError.MaxMessageLength);
        McpErrorEnvelopeAssert.Message(McpToolError.Serialize("NOT_FOUND", "producer text")).ShouldBe(McpErrorCodes.FixedMessage("NOT_FOUND"));
    }

    [Fact]
    public void Fail_UsesFixedMessage_AndSanitizesText()
    {
        McpErrorEnvelopeAssert.Message(McpToolError.Fail(McpErrorCodes.DatasetNotFound).ContentJson).ShouldBe("Dataset not found or not visible.");
        var custom = McpToolError.Fail(McpErrorCodes.InvalidParams, "bad\r\n" + new string('x', 1000));
        custom.IsSuccess.ShouldBeFalse();
        custom.ErrorMessage.ShouldBe(custom.ContentJson);
        McpErrorEnvelopeAssert.Detail(custom.ContentJson)!.Length.ShouldBeLessThanOrEqualTo(McpToolError.MaxMessageLength);
        McpErrorEnvelopeAssert.Detail(custom.ContentJson)!.ShouldNotContain("\n");
        McpErrorEnvelopeAssert.Detail(McpToolError.Fail(McpErrorCodes.DatasetNotFound, "caller text").ContentJson).ShouldBeNull();
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
    public void Normalize_TransportNotAllowed_KeepsCodeWithFixedText()
    {
        var mapped = new SqlErrorMapping(403, Autheris.Domain.Exceptions.TransportNotAllowedException.Code, "ignored upstream text");
        var json = McpToolError.Normalize(McpToolError.Serialize(McpToolError.FromSql(null, mapped)));

        using var doc = JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("TRANSPORT_NOT_ALLOWED");
        error.GetProperty("message").GetString().ShouldBe("Access to the data source is not allowed over this transport.");
        McpErrorCodes.IsCatalogueCode("TRANSPORT_NOT_ALLOWED").ShouldBeTrue();
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
        var foreign = McpToolError.Normalize("{\"error\":{\"code\":\"TOKEN_BUDGET_EXCEEDED\",\"message\":\"m\",\"hint\":\"" + longHint + "\"}}");
        var unknown = McpToolError.Normalize("{\"error\":{\"code\":\"NOT_IN_CATALOGUE\",\"message\":\"m\",\"hint\":\"" + longHint + "\"}}");

        // A hint the server did not write is dropped, whatever the code.
        ErrorOf(foreign).TryGetProperty("hint", out _).ShouldBeFalse();
        ErrorOf(unknown).TryGetProperty("hint", out _).ShouldBeFalse();
        var own = McpToolError.Serialize(new McpToolErrorBody("TOKEN_BUDGET_EXCEEDED", "m") { Hint = longHint });
        ErrorOf(own).GetProperty("hint").GetString()!.Length.ShouldBeLessThanOrEqualTo(McpToolError.MaxHintLength);
        McpToolError.Normalize(own).ShouldBe(own);
        // A server hint on a code that carries none is dropped as well.
        ErrorOf(McpToolError.Serialize(new McpToolErrorBody("DATASET_NOT_FOUND", "m") { Hint = "server hint" })).TryGetProperty("hint", out _).ShouldBeFalse();
    }

    [Fact]
    public void Normalize_Detail_OnlyForServerTexts_AndOnlyForInvalidParamsOrQuery()
    {
        var own = McpToolError.Serialize("INVALID_PARAMS", "Argument 'x' is required.");

        McpToolError.Normalize(own).ShouldBe(own);
        var forged = McpToolError.Normalize("{\"error\":{\"code\":\"INVALID_PARAMS\",\"message\":\"m\",\"detail\":\"table secret.salary missing\"}}");
        McpErrorEnvelopeAssert.Detail(forged).ShouldBeNull();
        var wrongCode = McpToolError.Normalize("{\"error\":{\"code\":\"DATASET_NOT_FOUND\",\"message\":\"m\",\"detail\":\"Argument 'x' is required.\"}}");
        McpErrorEnvelopeAssert.Detail(wrongCode).ShouldBeNull();
    }

    [Fact]
    public void GraphQlErrorPayload_ContainsOnlyFilteredMessagePathAndCode()
    {
        using var result = JsonDocument.Parse("""
            {"data":{"orders":[{"ssn":"123-45-6789"}]},"extensions":{"cost":5},
             "errors":[{"message":"The query is invalid or references fields that are not available.","path":["orders",0,"ssn"],
                        "locations":[{"line":1,"column":2}],"extensions":{"code":"INVALID_QUERY","stack":"at Foo.Bar","password":"p"}}]}
            """);

        var json = McpToolError.Serialize(new McpToolErrorBody("EXECUTION_FAILED", "m") { GraphQL = result.RootElement.Clone() });

        var graphQl = ErrorOf(json).GetProperty("graphQL");
        Members(graphQl).ShouldBe(["errors"]);
        var error = graphQl.GetProperty("errors")[0];
        Members(error).ShouldBe(["message", "path", "extensions"]);
        Members(error.GetProperty("extensions")).ShouldBe(["code"]);
        error.GetProperty("extensions").GetProperty("code").GetString().ShouldBe("INVALID_QUERY");
        json.ShouldNotContain("123-45-6789");
        json.ShouldNotContain("locations");
        json.ShouldNotContain("stack");
        json.ShouldNotContain("password");
        json.ShouldNotContain("\"data\"");
        McpToolError.Normalize(json).ShouldBe(json);

        // Only the codes of a failed execution carry a GraphQL payload.
        ErrorOf(McpToolError.Serialize(new McpToolErrorBody("DATASET_NOT_FOUND", "m") { GraphQL = result.RootElement.Clone() })).TryGetProperty("graphQL", out _).ShouldBeFalse();
    }

    [Fact]
    public void FailAdmin_AlwaysUsesTheFixedMessage_NeverTheAdminText()
    {
        var leaked = McpToolError.FailAdmin("Plan 'plan_secret' has a datasource password=hunter2.");

        McpErrorEnvelopeAssert.Code(leaked).ShouldBe("ADMIN_OPERATION_REJECTED");
        McpErrorEnvelopeAssert.Message(leaked.ContentJson).ShouldBe(McpErrorCodes.FixedMessage(McpErrorCodes.AdminOperationRejected));
        leaked.ContentJson.ShouldNotContain("hunter2");
        leaked.ContentJson.ShouldNotContain("plan_secret");
    }
}
