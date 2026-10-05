using Autheris.Application.SqlEndpoints.Services;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>Parameters in OFFSET / FETCH / LIMIT positions must not break the AST analysis of declarative endpoints.</summary>
public class SqlParameterPagingTests
{
    private const string PagedQuery = """
        -- @name active_notifications
        -- @summary Paged notifications
        -- @param offset: int = 0
        -- @param limit: int = 50
        SELECT n.id, n.title
        FROM notifications.items n
        WHERE n.status = @status
        ORDER BY n.id
        OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        """;

    [Fact]
    public void NormalizeForAst_RowCountPositions_BecomeQuestionMarks()
    {
        string normalized = SqlParameterExtractor.NormalizeForAst(
            "SELECT id FROM t WHERE a = @a ORDER BY id OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY");

        normalized.ShouldContain("OFFSET ? ROWS FETCH NEXT ? ROWS ONLY");
        normalized.ShouldContain("a = __param_a"); // ordinary parameters stay identifiers
    }

    [Theory]
    [InlineData("SELECT id FROM t LIMIT @n", "LIMIT ?")]
    [InlineData("SELECT id FROM t ORDER BY id OFFSET @o ROW FETCH FIRST @n ROW ONLY", "OFFSET ? ROW FETCH FIRST ? ROW ONLY")]
    [InlineData("select id from t order by id offset @o rows", "offset ? rows")]
    public void NormalizeForAst_CoversKeywordVariants(string sql, string expected)
    {
        SqlParameterExtractor.NormalizeForAst(sql).ShouldContain(expected);
    }

    [Fact]
    public void NormalizeForAst_StringLiteralWithKeyword_IsNotTouched()
    {
        string normalized = SqlParameterExtractor.NormalizeForAst("SELECT id FROM t WHERE note = 'OFFSET @x' AND a = @a");
        normalized.ShouldContain("'OFFSET @x'");
        normalized.ShouldContain("a = __param_a");
    }

    [Fact]
    public void NormalizedPagedQuery_IsParsableByTheEngine()
    {
        string normalized = SqlParameterExtractor.NormalizeForAst(PagedQuery);
        Should.NotThrow(() => new FastSqlEngine().Parse(normalized.AsMemory()));
    }

    [Fact]
    public void SqlEndpointLoader_LoadsPagedEndpointWithAllParameters()
    {
        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);

        var endpoint = loader.ParseSqlContent(PagedQuery, "fallback");

        endpoint.Name.ShouldBe("active_notifications");
        endpoint.Parameters.Select(p => p.Name).ShouldBe(new[] { "status", "offset", "limit" }, ignoreOrder: true);
        endpoint.Parameters.Single(p => p.Name == "limit").ClrType.ShouldBe(typeof(int));
    }
}
