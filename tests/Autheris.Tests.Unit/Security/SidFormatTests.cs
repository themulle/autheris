using Autheris.Domain.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class SidFormatTests
{
    [Theory]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-512")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-12-1-1234567890-2345678901-3456789012-4294967295")]
    [InlineData("S-1-1-0")]
    public void IsCanonical_accepts_domain_and_cloud_sids(string sid) => SidFormat.IsCanonical(sid).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-5")]
    [InlineData("S-1-5-")]
    [InlineData("S-1-5-21--1")]
    [InlineData("s-1-5-21-1")]
    [InlineData("S-2-5-21-1")]
    [InlineData("S-1-123-21-1")]
    [InlineData("S-1-5-21-12345678901")]
    [InlineData("S-1-5-21-1 ")]
    [InlineData("S-1-5-21-１２３")]
    [InlineData("S-1-5-21-FORWARD-GRP-finance")]
    [InlineData("S-1-5-21-FORWARD-USR-alice")]
    public void IsCanonical_rejects_non_ascii_overlong_and_pseudo_values(string? sid) => SidFormat.IsCanonical(sid).ShouldBeFalse();

    [Fact]
    public void IsCanonical_bounds_length_and_subauthority_count()
    {
        SidFormat.IsCanonical("S-1-5" + string.Concat(Enumerable.Repeat("-1", 15))).ShouldBeTrue();
        SidFormat.IsCanonical("S-1-5" + string.Concat(Enumerable.Repeat("-1", 16))).ShouldBeFalse();
        SidFormat.IsCanonical("S-1-5-21-" + new string('1', 200)).ShouldBeFalse();
    }
}
