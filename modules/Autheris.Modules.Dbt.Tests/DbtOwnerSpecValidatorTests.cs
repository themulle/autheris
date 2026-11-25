using System.Text.Json;
using Autheris.Modules.Dbt;
using Shouldly;
using Xunit;

namespace Autheris.Modules.Dbt.Tests;

public sealed class DbtOwnerSpecValidatorTests
{
    [Theory]
    [InlineData("""{"table":"t","owner":"S-1-5-21-1"}""")]
    [InlineData("""{"table":"t","owners":[]}""")]
    [InlineData("""{"table":"t","group":"g"}""")]
    [InlineData("""{"table":"t","groups":["g"]}""")]
    public void HasOwnerKeys_DetectsOwnerOwnersAndGroup(string json)
    {
        using var doc = JsonDocument.Parse(json);
        DbtOwnerSpecValidator.HasOwnerKeys(doc.RootElement).ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"table":"t"}""")]
    [InlineData("""["owner"]""")]
    public void HasOwnerKeys_IgnoresItemsWithoutOwnerKeys(string json)
    {
        using var doc = JsonDocument.Parse(json);
        DbtOwnerSpecValidator.HasOwnerKeys(doc.RootElement).ShouldBeFalse();
    }
}
