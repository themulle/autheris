namespace TrinoSqlEngine;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Analysis;
using Xunit;

public sealed class SqlParameterExtractorTests
{
    private readonly FastSqlEngine _engine = new();

    [Fact]
    public void ExtractTokens_FindsAtParametersAndMustacheParameters()
    {
        const string sql = @"
            SELECT id, name, revenue
            FROM customers
            WHERE country = @country 
              AND created_at >= @fromDate 
              AND status = {{status}};";

        var parameters = SqlParameterExtractor.ExtractTokens(sql);

        Assert.Equal(3, parameters.Count);
        Assert.Equal("country", parameters[0].Name);
        Assert.Equal("@country", parameters[0].Token);

        Assert.Equal("fromDate", parameters[1].Name);
        Assert.Equal("@fromDate", parameters[1].Token);

        Assert.Equal("status", parameters[2].Name);
        Assert.Equal("{{status}}", parameters[2].Token);
    }

    [Fact]
    public void NormalizeForAst_ReplacesTokensWithAstSafeIdentifiers()
    {
        const string sql = "SELECT * FROM orders WHERE id = @orderId AND total >= {{minTotal}}";
        string normalized = SqlParameterExtractor.NormalizeForAst(sql);

        Assert.Contains("id = __param_orderId", normalized);
        Assert.Contains("total >= __param_minTotal", normalized);
        Assert.DoesNotContain("@orderId", normalized);
        Assert.DoesNotContain("{{minTotal}}", normalized);
    }

    [Fact]
    public void AnalyzeAstParameters_InfersTargetColumnAndOperator()
    {
        const string rawSql = @"
            SELECT c.id, c.company_name, SUM(o.amount) AS total
            FROM customers c
            JOIN orders o ON c.id = o.customer_id
            WHERE c.country = @country
              AND o.amount >= @minAmount
            GROUP BY c.id, c.company_name;";

        var initial = SqlParameterExtractor.ExtractTokens(rawSql);
        string normalized = SqlParameterExtractor.NormalizeForAst(rawSql);

        var (tree, _) = _engine.Parse(normalized.AsMemory());
        var enriched = SqlParameterExtractor.AnalyzeAstParameters(tree, initial);

        var countryParam = enriched.FirstOrDefault(p => p.Name == "country");
        Assert.NotNull(countryParam);
        Assert.Equal("country", countryParam.TargetColumn);
        Assert.Equal("c", countryParam.TargetTable);
        Assert.Equal("=", countryParam.ComparisonOperator);

        var minAmountParam = enriched.FirstOrDefault(p => p.Name == "minAmount");
        Assert.NotNull(minAmountParam);
        Assert.Equal("amount", minAmountParam.TargetColumn);
        Assert.Equal("o", minAmountParam.TargetTable);
        Assert.Equal(">=", minAmountParam.ComparisonOperator);
    }

    private IReadOnlyList<ExtractedParameter> Analyze(string rawSql)
    {
        var tokens = SqlParameterExtractor.ExtractTokens(rawSql);
        var (tree, _) = _engine.Parse(SqlParameterExtractor.NormalizeForAst(rawSql).AsMemory());
        return SqlParameterExtractor.AnalyzeAstParameters(tree, tokens);
    }

    [Theory]
    [InlineData("SELECT id FROM t WHERE status IN (@ids)", "status", "IN")]
    [InlineData("SELECT id FROM t WHERE status NOT IN (@ids)", "status", "NOT IN")]
    [InlineData("SELECT id FROM t x WHERE x.status IN (@ids)", "status", "IN")]
    public void AnalyzeAstParameters_AssociatesTheSoleInListItemWithItsColumnAndOperator(string sql, string column, string op)
    {
        var p = Analyze(sql).Single(x => x.Name == "ids");
        Assert.Equal(column, p.TargetColumn);
        Assert.Equal(op, p.ComparisonOperator);
    }

    [Theory]
    [InlineData("SELECT id FROM t WHERE status IN (@ids, 'x')")]
    [InlineData("SELECT id FROM t WHERE status IN (@ids, @other)")]
    public void AnalyzeAstParameters_DoesNotAssociateAParameterThatSharesTheInList(string sql)
    {
        var p = Analyze(sql).Single(x => x.Name == "ids");
        Assert.Null(p.TargetColumn);
    }
}
