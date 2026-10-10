namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Catalog.Search;
using Shouldly;
using Xunit;

public sealed class SmartSchemaTokenizerTests
{
    [Fact]
    public void Tokenize_EmptyOrNull_ReturnsEmptyArray()
    {
        SmartSchemaTokenizer.Tokenize(null).ShouldBeEmpty();
        SmartSchemaTokenizer.Tokenize("").ShouldBeEmpty();
        SmartSchemaTokenizer.Tokenize("   ").ShouldBeEmpty();
    }

    [Fact]
    public void Tokenize_SnakeCase_ExtractsComponents()
    {
        var tokens = SmartSchemaTokenizer.Tokenize("tbl_customer_invoices_daily", expandAbbreviations: false);
        tokens.ShouldContain("tbl");
        tokens.ShouldContain("customer");
        tokens.ShouldContain("invoices");
        tokens.ShouldContain("daily");
    }

    [Fact]
    public void Tokenize_CamelCaseAndPascalCase_ExtractsComponents()
    {
        var tokens = SmartSchemaTokenizer.Tokenize("MonthlySalesTransactionsReport", expandAbbreviations: false);
        tokens.ShouldContain("monthly");
        tokens.ShouldContain("sales");
        tokens.ShouldContain("transactions");
        tokens.ShouldContain("report");
    }

    [Fact]
    public void Tokenize_ExpandsKnownAbbreviations()
    {
        var tokens = SmartSchemaTokenizer.Tokenize("fct_inv_cust_rev", expandAbbreviations: true);
        // fct -> fact, fakten
        tokens.ShouldContain("fact");
        // inv -> invoice, rechnung, faktura
        tokens.ShouldContain("invoice");
        tokens.ShouldContain("rechnung");
        // cust -> customer, kunde
        tokens.ShouldContain("customer");
        tokens.ShouldContain("kunde");
        // rev -> revenue, umsatz, erloes
        tokens.ShouldContain("revenue");
        tokens.ShouldContain("umsatz");
    }

    [Fact]
    public void Tokenize_FiltersStopWords()
    {
        var tokens = SmartSchemaTokenizer.Tokenize("orders for the customer with invoice and details", expandAbbreviations: false);
        tokens.ShouldNotContain("for");
        tokens.ShouldNotContain("the");
        tokens.ShouldNotContain("with");
        tokens.ShouldNotContain("and");
        tokens.ShouldContain("orders");
        tokens.ShouldContain("customer");
        tokens.ShouldContain("invoice");
        tokens.ShouldContain("details");
    }
}
