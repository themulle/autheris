namespace Autheris.Tests.Unit.Sql;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// Contract for the REST dataset OFFSET pushdown: the Trino-form statement the data service emits
/// (<c>OFFSET n ROWS LIMIT m</c>) compiles for every executable dialect into that dialect's native pagination.
/// </summary>
public sealed class RestOffsetDialectContractTests
{
    private const string Tenant = "tenant_a";
    private const string RestStatement = "SELECT id, dept FROM orders ORDER BY id OFFSET 10 ROWS LIMIT 51";

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-AST"), new Claim("tenant_id", Tenant)], "Test"));

    [Theory]
    [InlineData("PostgreSQL", "LIMIT", "OFFSET")]
    [InlineData("Sqlite", "LIMIT", "OFFSET")]
    [InlineData("SqlServer", "FETCH NEXT", "OFFSET")]
    public async Task RestStatement_CompilesToNativePagination(string sourceType, params string[] expectedKeywords)
    {
        var compiled = await AstCompilerWebSqlTests.CreateService(sourceType)
            .CompileAsync(RestStatement, User(), new TenantId(Tenant));

        foreach (var keyword in expectedKeywords)
        {
            compiled.Sql.ShouldContain(keyword, Case.Insensitive);
        }

        // Offset and page size travel as structural integers (bound literals), never as client text.
        var literals = compiled.ValuesOf(ParameterOrigin.QueryLiteral).Select(Convert.ToInt64).ToList();
        literals.ShouldContain(10L);
        literals.ShouldContain(51L);
    }
}
