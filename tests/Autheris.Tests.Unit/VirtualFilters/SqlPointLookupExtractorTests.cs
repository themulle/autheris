namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using Autheris.Application.Sql.Services;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

public sealed class SqlPointLookupExtractorTests
{
    private readonly ISqlEngine _sqlEngine = FastSqlEngine.Default;

    [Fact]
    public void ExtractPointLookups_SingleEquality_ExtractsColumnAndValue()
    {
        var sql = "SELECT * FROM web_service.customers WHERE id = 47";
        var (tree, _) = _sqlEngine.Parse(sql.AsMemory());
        var ast = new SqlAstBuilder().BuildStatement(tree);

        var lookups = SqlPointLookupExtractor.ExtractPointLookups(ast);

        lookups.ContainsKey("id").ShouldBeTrue();
        lookups["id"].ShouldBe("47");
    }

    [Fact]
    public void ExtractPointLookups_InvertedEquality_ExtractsColumnAndValue()
    {
        var sql = "SELECT * FROM web_service.customers WHERE 'cust_99' = customer_code";
        var (tree, _) = _sqlEngine.Parse(sql.AsMemory());
        var ast = new SqlAstBuilder().BuildStatement(tree);

        var lookups = SqlPointLookupExtractor.ExtractPointLookups(ast);

        lookups.ContainsKey("customer_code").ShouldBeTrue();
        lookups["customer_code"].ShouldBe("cust_99");
    }

    [Fact]
    public void ExtractPointLookups_CompositeAndEquality_ExtractsAll()
    {
        var sql = "SELECT * FROM web_service.orders WHERE tenant_id = 't-123' AND client_id = 456";
        var (tree, _) = _sqlEngine.Parse(sql.AsMemory());
        var ast = new SqlAstBuilder().BuildStatement(tree);

        var lookups = SqlPointLookupExtractor.ExtractPointLookups(ast);

        lookups["tenant_id"].ShouldBe("t-123");
        lookups["client_id"].ShouldBe("456");
    }

    [Fact]
    public void ExtractPointLookups_NonEquality_Ignored()
    {
        var sql = "SELECT * FROM web_service.orders WHERE amount > 100";
        var (tree, _) = _sqlEngine.Parse(sql.AsMemory());
        var ast = new SqlAstBuilder().BuildStatement(tree);

        var lookups = SqlPointLookupExtractor.ExtractPointLookups(ast);

        lookups.ShouldBeEmpty();
    }
}
