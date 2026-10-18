namespace Autheris.Tests.Unit.Identity.Attributes;

using System.Collections.Generic;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class CallerAttributeDefinitionTests
{
    [Theory]
    [InlineData("a", true)]
    [InlineData("cost_center", true)]
    [InlineData("dept2", true)]
    [InlineData("cost__center", false)]
    [InlineData("_cost", false)]
    [InlineData("cost_", false)]
    [InlineData("Cost", false)]
    [InlineData("1cost", false)]
    [InlineData("cost center", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Name_Grammar(string? name, bool valid) => CallerAttributeDefinition.IsValidName(name).ShouldBe(valid);

    [Fact]
    public void Name_Of65Characters_IsRefused() => CallerAttributeDefinition.IsValidName(new string('a', 65)).ShouldBeFalse();

    [Fact]
    public void Name_Of64Characters_IsAccepted() => CallerAttributeDefinition.IsValidName(new string('a', 64)).ShouldBeTrue();

    [Fact]
    public void Hash_CoversProviderConfiguration_ButNotIdOrStatus()
    {
        var a = SqliteCallerAttributeRepositoryTests.Definition();
        var sameMeaning = a with { Id = System.Guid.NewGuid(), Status = FilterApprovalStatus.Active };
        var otherClaim = a with { Provider = new CallerAttributeProviderBinding("claims", new Dictionary<string, string> { ["claim"] = "other" }) };

        sameMeaning.ComputeDefinitionHash().ShouldBe(a.ComputeDefinitionHash());
        otherClaim.ComputeDefinitionHash().ShouldNotBe(a.ComputeDefinitionHash());
        a.ComputeDefinitionHash().Length.ShouldBe(64);
    }

    [Fact]
    public void Hash_IsLengthPrefixed_SoShiftedFieldsDoNotCollide()
    {
        var a = SqliteCallerAttributeRepositoryTests.Definition(description: "ab");
        var b = a with { Name = "cost_centerab", Description = string.Empty };
        a.ComputeDefinitionHash().ShouldNotBe(b.ComputeDefinitionHash());
    }
}
