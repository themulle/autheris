namespace Autheris.Tests.Unit.Mcp;

using Autheris.Application.Sql.Errors;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>query_sql answers a statement it cannot parse with the code and position WebSQL returns for it, and never with parser text.</summary>
[Trait("Category", "Security")]
public sealed class McpQuerySqlErrorTests
{
    private const string Broken = "SELECT FROM WHERE ((";

    private static string Args(string sql) => System.Text.Json.JsonSerializer.Serialize(new { sql });

    private static async Task<SqlErrorMapping?> MapperResult(string sql)
    {
        try
        {
            await FastSqlEngine.Default.AnalyzeAsync(sql.AsMemory(), null, CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return SqlCompilerErrorMapper.TryMap(ex, out var mapped) ? mapped : null;
        }
    }

    [Fact]
    public async Task QuerySql_SyntaxError_LineColumnEqualWebSqlMapper_NoParserText()
    {
        var rig = McpErrorTestRig.Create(tables: [McpErrorTestRig.Active("orders")]);
        var mapped = await MapperResult(Broken);

        var result = await rig.Call("query_sql", Args(Broken));

        result.IsSuccess.ShouldBeFalse();
        var error = McpErrorEnvelopeAssert.Error(result.ContentJson);
        if (mapped is { } m)
        {
            error.GetProperty("code").GetString().ShouldBe(m.Code);
            error.GetProperty("message").GetString().ShouldBe(m.Title);
            if (m.Line is { } line && m.Column is { } column)
            {
                error.GetProperty("line").GetInt32().ShouldBe(line);
                error.GetProperty("column").GetInt32().ShouldBe(column);
            }
        }
        else
        {
            error.GetProperty("code").GetString().ShouldBe(GovernedSqlErrorCodes.SyntaxError);
            error.TryGetProperty("line", out _).ShouldBeFalse();
        }

        result.ContentJson.ShouldNotContain("could not be resolved");
        result.ContentJson.ShouldNotContain("FROM WHERE");
        result.ContentJson.ShouldNotContain("SQL_SYNTAX_ERROR: ");
        rig.Executor.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task QuerySql_SyntaxError_SamePositionForHiddenAndUnknownTable()
    {
        var rig = McpErrorTestRig.Create(
            decide: McpErrorTestRig.Deny,
            tables: [McpErrorTestRig.Active("secret")]);

        var hidden = await rig.Call("query_sql", Args("SELECT * FROM sales.public.secret WHERE (("));
        var unknown = await rig.Call("query_sql", Args("SELECT * FROM sales.public.ghosts WHERE (("));
        var allowed = await rig.Call("query_sql", Args("SELECT * FROM sales.public.orders WHERE (("));

        hidden.ContentJson.ShouldBe(unknown.ContentJson);
        hidden.ContentJson.ShouldBe(allowed.ContentJson);
        McpErrorEnvelopeAssert.Code(hidden.ContentJson).ShouldStartWith("SQL_");
    }

    [Fact]
    public async Task QuerySql_UnknownVsDeniedTable_ByteIdentical_SqlPolicyDenied()
    {
        var rig = McpErrorTestRig.Create(
            decide: McpErrorTestRig.Deny,
            tables: [McpErrorTestRig.Active("secret")]);

        var denied = await rig.Call("query_sql", Args("SELECT * FROM sales.public.secret"));
        var unknown = await rig.Call("query_sql", Args("SELECT * FROM sales.public.ghosts"));

        denied.ContentJson.ShouldBe(unknown.ContentJson);
        McpErrorEnvelopeAssert.Code(denied).ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }
}
