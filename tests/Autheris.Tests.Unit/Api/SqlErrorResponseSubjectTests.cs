namespace Autheris.Tests.Unit.Api;

using System.Collections.Generic;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Application.Sql.Errors;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;
using Xunit;

/// <summary>The problem body of a named rejection: name, position and hint as scalar extensions; an unnamed rejection carries none.</summary>
public sealed class SqlErrorResponseSubjectTests
{
    private static Microsoft.AspNetCore.Mvc.ProblemDetails Body(System.Exception ex)
    {
        SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        Dictionary<string, object?>? ext = null;
        SqlErrorResponse.AddMappedExtensions(ref ext, mapped);
        return ApiProblems.Problem(mapped.Status, mapped.Code, mapped.Title, extraExtensions: ext).ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
    }

    [Fact]
    public void FunctionNotPermitted_BodyShape()
    {
        var body = Body(new FunctionNotPermittedException("pg_sleep", 1, 8));

        body.Status.ShouldBe(403);
        body.Extensions["code"].ShouldBe("SQL_FUNCTION_NOT_PERMITTED");
        body.Extensions["function"].ShouldBe("pg_sleep");
        body.Extensions["line"].ShouldBe(1);
        body.Extensions["column"].ShouldBe(8);
        body.Extensions["hint"].ShouldBe("The function 'pg_sleep' is not permitted for this data source. Remove it or use a permitted function.");
    }

    [Fact]
    public void UnknownColumn_BodyShape()
    {
        var body = Body(new SqlUnknownColumnException("custmer_id", 1, 8, TargetSqlDialect.PostgreSql));

        body.Status.ShouldBe(400);
        body.Extensions["code"].ShouldBe("UNKNOWN_COLUMN");
        body.Extensions["columnName"].ShouldBe("custmer_id");
        body.Extensions.ContainsKey("function").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void UnsafeFunctionName_BodyEqualsUnnamedVariant()
    {
        var body = Body(new FunctionNotPermittedException("a b", 1, 8));

        body.Extensions.ContainsKey("function").ShouldBeFalse();
        body.Extensions.ContainsKey("hint").ShouldBeFalse();
        body.Extensions.ContainsKey("line").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PolicyDenied_Body_HasNoLineColumnHintColumnName()
    {
        var body = Body(new SqlUnknownColumnException("bad name", 1, 8, TargetSqlDialect.PostgreSql));

        body.Extensions["code"].ShouldBe("SQL_POLICY_DENIED");
        foreach (var key in new[] { "line", "column", "hint", "columnName", "function", "construct" })
        {
            body.Extensions.ContainsKey(key).ShouldBeFalse(key);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AllowedExtensionKeys_ContainOnlyAgreedNewKeys()
    {
        // Free-text keys must stay dropped; exactly the three name keys were added.
        var ext = new Dictionary<string, object?> { ["function"] = "f", ["construct"] = "c", ["columnName"] = "n", ["name"] = "x", ["detail"] = "x", ["message"] = "x", ["tableName"] = "x" };

        var body = ApiProblems.Problem(400, "UNKNOWN_COLUMN", "x", extraExtensions: ext).ShouldBeOfType<ProblemHttpResult>().ProblemDetails;

        foreach (var kept in new[] { "function", "construct", "columnName" })
        {
            body.Extensions.ContainsKey(kept).ShouldBeTrue(kept);
        }
        foreach (var dropped in new[] { "name", "detail", "message", "tableName" })
        {
            body.Extensions.ContainsKey(dropped).ShouldBeFalse(dropped);
        }
    }
}
