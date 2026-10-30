namespace Autheris.Tests.Unit.Tenancy;

using Autheris.Domain.Common;
using Shouldly;
using Xunit;

/// <summary>DT-SC-07 / DT-A-6: the whole <c>_</c> prefix is reserved; <c>_platform</c> exists only via <see cref="TenantId.Platform"/>.</summary>
[Trait("Category", "Security")]
public sealed class TenantIdReservedPrefixTests
{
    [Theory]
    [InlineData("_platform")]
    [InlineData("_PLATFORM")]
    [InlineData("_Platform")]
    [InlineData("_x")]
    [InlineData("_")]
    public void Reserved_Prefix_Rejected_By_TryParse_And_Constructor(string value)
    {
        TenantId.TryParse(value, out var parsed).ShouldBeFalse();
        parsed.ShouldBe(default);
        TenantId.IsReserved(value).ShouldBeTrue();
        Should.Throw<ArgumentException>(() => new TenantId(value));
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("a_b")]
    [InlineData("default-tenant")]
    [InlineData("x_")]
    public void Ordinary_Ids_Are_Not_Reserved(string value)
    {
        TenantId.IsReserved(value).ShouldBeFalse();
        TenantId.TryParse(value, out var parsed).ShouldBeTrue();
        parsed.Value.ShouldBe(value);
    }

    [Fact]
    public void Platform_Factory_Is_The_Only_Way_To_Get_The_Platform_Tenant()
    {
        TenantId.Platform.Value.ShouldBe("_platform");
        TenantId.IsReserved(TenantId.Platform.Value).ShouldBeTrue();
        TenantId.IsReserved(TenantId.Platform).ShouldBeTrue();
        TenantId.IsReserved(new TenantId("acme")).ShouldBeFalse();
        TenantId.IsReserved(default(TenantId)).ShouldBeFalse();
    }
}
