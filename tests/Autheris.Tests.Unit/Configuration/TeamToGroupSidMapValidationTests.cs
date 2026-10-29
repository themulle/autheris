namespace Autheris.Tests.Unit.Configuration;

using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>NL-SC-09 / NL-T-09 (O-5f): <c>OpenMetadata:TeamToGroupSidMap</c> is validated at start and never partially accepted.</summary>
[Trait("Category", "Security")]
public sealed class TeamToGroupSidMapValidationTests
{
    private const string Guid1 = "11111111-1111-1111-1111-111111111111";

    private static string[] Failures(Dictionary<string, string> map, string environmentName = "Production")
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        var result = new GatewayCoreOptionsValidator(environment).Validate(null,
            new GatewayOptions { OpenMetadata = new OpenMetadataOptions { TeamToGroupSidMap = map } });
        return result.Failures?.Where(f => f.Contains("TeamToGroupSidMap")).ToArray() ?? [];
    }

    [Fact]
    public void Valid_Prefixed_Map_Passes()
    {
        Failures(new() { ["id:" + Guid1] = "S-1-5-21-5001", ["fqn:org.finance"] = "S-1-5-21-5002", ["name:Finance"] = "S-1-5-21-5003" }).ShouldBeEmpty();
        Failures([]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("FinanceTeam", "S-1-5-21-5001")]                          // unprefixed name
    [InlineData(Guid1, "S-1-5-21-5001")]                                  // unprefixed GUID
    [InlineData("id:not-a-guid", "S-1-5-21-5001")]                        // invalid GUID
    [InlineData("id:00000000-0000-0000-0000-000000000000", "S-1-5-21-5001")]
    [InlineData("fqn:", "S-1-5-21-5001")]                                 // empty key value
    [InlineData("name:Finance", "")]                                      // empty value
    [InlineData("name:Finance", "not-a-sid-SECRETVALUE")]                 // bad SID
    public void OpenMetadata_Unprefixed_Team_Key_Fails_Validation(string key, string sid)
    {
        var failures = Failures(new() { [key] = sid });

        failures.ShouldNotBeEmpty();
        failures.ShouldAllBe(f => f.Contains("Gateway:OpenMetadata:TeamToGroupSidMap entry #1"));
        failures.ShouldAllBe(f => !f.Contains("SECRETVALUE") && (sid.Length == 0 || !f.Contains(sid)));
    }

    [Fact]
    public void OpenMetadata_Team_Key_Duplicate_Resolving_To_The_Same_Team_Fails_Validation()
    {
        var failures = Failures(new() { ["id:" + Guid1] = "S-1-5-21-5001", ["ID:" + Guid1.ToUpperInvariant()] = "S-1-5-21-5002" });

        failures.ShouldHaveSingleItem().ShouldContain("entry #2");
        failures[0].ShouldNotContain("S-1-5-21");
    }

    [Fact]
    public void Validation_Applies_In_Development_As_Well()
    {
        Failures(new() { ["Unprefixed"] = "S-1-5-21-5001" }, "Development").ShouldNotBeEmpty();
    }
}
