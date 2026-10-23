namespace Autheris.Tests.Unit.DataApi;

using Autheris.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>#313 / #301 (negatives), SEC-PG-14: strict, overflow-free parse of the REST <c>limit</c> and <c>offset</c> parameters.</summary>
[Trait("Category", "Security")]
public sealed class DatasetPagingParseTests
{
    private static bool Parse(string query, out int limit, out int offset)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(query);
        return DatasetDataEndpoints.TryParsePaging(ctx.Request, out limit, out offset);
    }

    [Theory]
    [InlineData("", 50, 0)]
    [InlineData("?limit=0", 0, 0)]
    [InlineData("?limit=7&offset=12", 7, 12)]
    [InlineData("?limit=0007", 7, 0)]
    [InlineData("?offset=2147483647", 50, int.MaxValue)]
    public void ValidValues_AreAccepted(string query, int limit, int offset)
    {
        Parse(query, out var l, out var o).ShouldBeTrue();
        l.ShouldBe(limit);
        o.ShouldBe(offset);
    }

    [Theory]
    [InlineData("?limit=abc")]
    [InlineData("?limit=-1")]
    [InlineData("?offset=-1")]
    [InlineData("?limit=%2B5")]
    [InlineData("?limit=%205")]
    [InlineData("?limit=5%20")]
    [InlineData("?limit=1e3")]
    [InlineData("?limit=1.5")]
    [InlineData("?limit=")]
    [InlineData("?limit=2147483648")]
    [InlineData("?offset=99999999999999999999")]
    [InlineData("?limit=00000000000000000000005")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?offset=1&offset=2")]
    [InlineData("?limit=%D9%A5")]
    public void Malformed_AreRejected_WithoutThrowing(string query)
    {
        Parse(query, out _, out _).ShouldBeFalse();
    }
}
