namespace Autheris.Tests.Unit.GraphQL;

using System.Text.Json;
using Autheris.GraphQL.Mcp;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;
using Xunit;

/// <summary>The MCP error text of a named rejection carries the sanitized name only as data inside the fixed hint template.</summary>
public sealed class McpSqlErrorTextTests
{
    private static JsonElement Error(System.Exception ex) =>
        JsonDocument.Parse(GatewayMcpQueryExecutor.CreateMappedErrorResult("tenant_a", "run_sql", ex)).RootElement.GetProperty("error");

    [Fact]
    [Trait("Category", "Security")]
    public void FunctionNotPermitted_McpText_ContainsOnlySanitizedName()
    {
        var named = Error(new FunctionNotPermittedException("pg_sleep", 1, 8));
        named.GetProperty("code").GetString().ShouldBe("SQL_FUNCTION_NOT_PERMITTED");
        named.GetProperty("message").GetString().ShouldBe(GovernedSqlErrorCodes.FixedMessage("SQL_FUNCTION_NOT_PERMITTED"));
        named.GetProperty("hint").GetString().ShouldBe("The function 'pg_sleep' is not permitted for this data source. Remove it or use a permitted function.");

        // a name outside the allowed charset is dropped together with the hint; nothing of it reaches the text
        var hostile = Error(new FunctionNotPermittedException("x'); <script>", 1, 8));
        hostile.TryGetProperty("hint", out var hint).ShouldBeFalse(hint.ToString());
        hostile.GetRawText().ShouldNotContain("script");
    }

    [Fact]
    public void UnknownColumn_McpText_NamesTheColumnInTheHint()
    {
        var error = Error(new SqlUnknownColumnException("custmer_id", 1, 8, TargetSqlDialect.PostgreSql));

        error.GetProperty("code").GetString().ShouldBe("UNKNOWN_COLUMN");
        error.GetProperty("hint").GetString()!.ShouldContain("'custmer_id'");
    }
}
