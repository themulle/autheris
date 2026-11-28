namespace Autheris.Tests.Unit.Sql;

using System;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>Function rejections of a client statement carry the function name and its position; policy text never names a function.</summary>
public sealed class WebSqlFunctionNamingTests
{
    private const string Tenant = "tenant_a";

    private static ClaimsPrincipal User() =>
        Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-AST"), new Claim("tenant_id", Tenant)], "Test")));

    private static Task<TrinoSqlEngine.Ast.Emit.CompiledSql> CompileAsync(string sourceType, string sql) =>
        AstCompilerWebSqlTests.CreateService(sourceType).CompileAsync(sql, User(), new TenantId(Tenant));

    [Fact]
    public async Task DisallowedFunction_ThrowsFunctionNotPermitted_WithNameAndPosition()
    {
        var ex = await Should.ThrowAsync<FunctionNotPermittedException>(() =>
            CompileAsync("PostgreSQL", "SELECT id,\n  pg_sleep(1) FROM orders"));

        ex.FunctionName.ShouldBe("pg_sleep");
        ex.Line.ShouldBe(2);
        ex.Column.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DisallowedFunction_ClientStatement_NamesFunctionFromClientText()
    {
        var ex = await Should.ThrowAsync<FunctionNotPermittedException>(() =>
            CompileAsync("PostgreSQL", "SELECT PG_Sleep(1) FROM orders"));

        // The name is the lower-cased call text of the client statement, never an exception message of another origin.
        ex.FunctionName.ShouldBe("pg_sleep");
        ex.Line.ShouldBe(1);
        ex.Column.ShouldBe(8);
    }

    [Fact]
    public async Task FunctionOnlyOnOtherDialect_SameCode()
    {
        // len() is a SQL Server function: refused (and named) on PostgreSQL, accepted on SQL Server.
        var ex = await Should.ThrowAsync<FunctionNotPermittedException>(() =>
            CompileAsync("PostgreSQL", "SELECT len(dept) FROM orders"));
        ex.FunctionName.ShouldBe("len");
        ex.Column.ShouldBe(8);

        var compiled = await CompileAsync("SqlServer", "SELECT len(dept) FROM orders");
        compiled.Sql.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void SqlQueryAnalyzer_FunctionCallSites_FirstOccurrenceOneBased()
    {
        var meta = FastSqlEngine.Default.Analyze("SELECT upper(a),\n    lower(b), upper(c) FROM t".AsMemory());

        meta.FunctionCallSites.ShouldNotBeNull();
        meta.FunctionCallSites!.Select(s => (s.Name, s.Line, s.Column))
            .ShouldBe([("upper", 1, 8), ("lower", 2, 5)]);
        meta.FunctionCalls.ShouldBe(["upper", "lower"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RowPolicy_DisallowedFunction_RejectedUnnamed()
    {
        var decision = TableAccessDecision.Allowed(
            new TableIdentifier("pg", "public", "orders"),
            new System.Collections.Generic.Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["id"] = ColumnAccessLevel.Clear },
            rowFilterSql: "pg_sleep(1) > 0",
            hasUnconstrainedColumnAllow: true);

        var ex = Should.Throw<SecurityException>(() => DecisionRowFilter.TryResolve(decision, SqlFunctionAllowlists.Ansi, out _));

        ex.ShouldNotBeOfType<FunctionNotPermittedException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RowPolicyWithDisallowedFunction_DoesNotNameFunction()
    {
        var ex = Should.Throw<SecurityException>(() => new SqlAstBuilder(new AstBuilderOptions
        {
            EnforceFunctionPolicy = true,
            AllowedFunctions = SqlFunctionAllowlists.Ansi
        }).BuildStandaloneExpression(FastSqlEngine.Default.ParseExpression("pg_sleep(1) > 0".AsMemory(), SqlTokenSecurityOptions.Strict).Item1));

        ex.ShouldNotBeOfType<FunctionNotPermittedException>();
        new AstBuilderOptions().ExposeRejectionDetails.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MaskExpressionWithDisallowedFunction_DoesNotNameFunction()
    {
        var parser = new PolicyExpressionParser(FastSqlEngine.Default);
        var context = new PolicyParseContext(
            new TableIdentity("public", "orders"),
            ["id"],
            SqlFunctionAllowlists.Ansi,
            new System.Collections.Generic.Dictionary<string, PolicyValue>(),
            1);

        var ex = Should.Throw<Exception>(() => parser.Parse("pg_sleep(1) > 0", context));

        ex.ShouldNotBeOfType<FunctionNotPermittedException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TableQualificationValidator_ClientText_NamesDisallowedFunction()
    {
        var ex = Should.Throw<FunctionNotPermittedException>(() =>
            TableQualificationValidator.Validate("SELECT pg_sleep(1) FROM orders"));

        ex.FunctionName.ShouldBe("pg_sleep");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("pg\nsleep")]
    [InlineData("\"quoted\"")]
    [InlineData("a<b>")]
    public void FunctionWithUnsafeName_FallsBackToUnnamedBody(string unsafeName)
    {
        Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.TryMap(new FunctionNotPermittedException(unsafeName, 1, 8), out var mapped).ShouldBeTrue();

        mapped.Code.ShouldBe("SQL_FUNCTION_NOT_PERMITTED");
        mapped.Subject.ShouldBeNull();
        mapped.Hint.ShouldBeNull();
        mapped.Line.ShouldBeNull();
        mapped.Title.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage("SQL_FUNCTION_NOT_PERMITTED"));
    }
}
