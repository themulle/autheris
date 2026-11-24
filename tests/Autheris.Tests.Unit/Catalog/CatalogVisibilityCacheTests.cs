namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Catalog.Search;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogVisibilityCacheTests
{
    [Fact]
    public void Key_includes_grant_epoch()
    {
        var principal = Wp0.User("S-1-5-21-1001", "tenant-a");

        var first = CatalogVisibilityCacheKey.From(principal, "sales", 1, 1, 1, grantEpoch: 1);
        var sameEpoch = CatalogVisibilityCacheKey.From(principal, "sales", 1, 1, 1, grantEpoch: 1);
        var afterGrantChange = CatalogVisibilityCacheKey.From(principal, "sales", 1, 1, 1, grantEpoch: 2);

        first.ShouldBe(sameEpoch);
        first.ShouldNotBe(afterGrantChange);
        first.GrantEpoch.ShouldBe(1);
    }
}
