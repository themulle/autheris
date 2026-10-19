namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>Issue #111 A-1 G1 / F-C11: the one shared shaping rule of MCP, GraphQL and the REST discovery service.</summary>
[Trait("Category", "Security")]
public sealed class CatalogViewShapingTests
{
    [Theory]
    [InlineData(CatalogView.Unmarked)]
    [InlineData(CatalogView.Granted)]
    [InlineData(CatalogView.Summary)]
    [InlineData(CatalogView.Full)]
    public void F_C11_Admin_GetsFullOutput_ForEveryView(CatalogView view) =>
        CatalogViewShaping.Resolve(isAdmin: true, view).ShouldBe(CatalogOutputShape.Full);

    [Theory]
    [InlineData(CatalogView.Granted, CatalogOutputShape.Full)]
    [InlineData(CatalogView.Full, CatalogOutputShape.Full)]
    [InlineData(CatalogView.Summary, CatalogOutputShape.Summary)]
    [InlineData(CatalogView.Unmarked, CatalogOutputShape.NotFound)]
    public void F_C11_NonAdmin_FollowsTheViewPositively(CatalogView view, CatalogOutputShape expected) =>
        CatalogViewShaping.Resolve(isAdmin: false, view).ShouldBe(expected);

    [Fact]
    public void UnknownCatalogView_TreatedAsNotFound()
    {
        CatalogViewShaping.Resolve(isAdmin: false, (CatalogView)99).ShouldBe(CatalogOutputShape.NotFound);
        CatalogViewShaping.Resolve(isAdmin: false, (CatalogView)(-1)).ShouldBe(CatalogOutputShape.NotFound);
    }
}
