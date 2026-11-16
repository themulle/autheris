namespace Autheris.Tests.Unit.OData;

using System.Linq;
using Autheris.Application.OData;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ODataRequiredFilterConstraintTests
{
    [Theory]
    [InlineData("region ne 'EU'")]
    [InlineData("region eq null")]
    [InlineData("null eq region")]
    [InlineData("region eq status")]
    [InlineData("tolower(region) eq 'eu'")]
    [InlineData("toupper(region) eq 'EU'")]
    [InlineData("contains(region, '%')")]
    [InlineData("startswith(region, 'E')")]
    [InlineData("endswith(region, 'U')")]
    [InlineData("not (region eq 'EU')")]
    [InlineData("region eq 'EU' or status eq 'open'")]
    public void WeakOrUnconstrainedODataFilter_DoesNotConstrainColumn(string filter)
    {
        var clause = ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql);

        clause.ConstrainedColumns.ShouldNotBeNull();
        clause.ConstrainedColumns.ShouldNotContain("region");
    }

    [Theory]
    [InlineData("region eq 'EU'")]
    [InlineData("'EU' eq region")]
    [InlineData("(region) eq ('EU')")]
    [InlineData("region gt 'A'")]
    [InlineData("region ge 'A'")]
    [InlineData("region lt 'Z'")]
    [InlineData("region le 'Z'")]
    [InlineData("region eq 'EU' or region eq 'US'")]
    [InlineData("region eq 'EU' and status ne 'closed'")]
    public void StrictODataFilter_ConstrainsColumn(string filter)
    {
        var clause = ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql);

        clause.ConstrainedColumns.ShouldNotBeNull();
        clause.ConstrainedColumns.ShouldContain("region");
    }

    [Fact]
    public void ChainNode_200_Term_And_Chain_ConstrainsEveryColumn()
    {
        var filter = string.Join(" and ", Enumerable.Range(0, 200).Select(i => $"c{i} eq {i}"));
        var clause = ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql);

        clause.ConstrainedColumns.ShouldNotBeNull();
        clause.ConstrainedColumns!.Count.ShouldBe(200);
        clause.ConstrainedColumns.ShouldContain("c199");
    }
}
