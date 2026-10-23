namespace Autheris.Tests.Unit.GraphQL;

using System.Collections.Generic;
using Autheris.GraphQL.Types;
using Shouldly;
using Xunit;

/// <summary>
/// GQL-02: the demo invoice mapping returns null for masked/denied/unparsable values and never invents data.
/// </summary>
public sealed class Gql02InvoiceMappingTests
{
    [Fact]
    public void GQL_02_MaskedValues_MapToNull_AndIdIsNotSynthesized()
    {
        var row = new Dictionary<string, object?> { ["amount"] = "****", ["name"] = null };

        var invoice = FinanceQuery.MapInvoice(row);

        invoice.Id.ShouldBeNull();
        invoice.Amount.ShouldBeNull();
        invoice.Vendor.ShouldBeNull();
    }

    [Theory]
    [InlineData(12.5d)]
    [InlineData(12.5f)]
    public void GQL_02_NonDecimalNumbers_AreConverted(object amount)
    {
        var invoice = FinanceQuery.MapInvoice(new Dictionary<string, object?> { ["id"] = 7L, ["amount"] = amount });

        invoice.Id.ShouldBe("7");
        invoice.Amount.ShouldBe(12.5m);
    }
}
