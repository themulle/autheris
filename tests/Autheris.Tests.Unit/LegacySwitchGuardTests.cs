using System.Reflection;
using Autheris.Api.Configuration;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>ADR-012 phase 4: removed domain-local warn_/danger_ keys must fail loudly, not be ignored.</summary>
public sealed class LegacySwitchGuardTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Theory]
    [InlineData("Mcp:danger_bypass_mcp_auth", "Gateway:Insecure:danger_bypass_mcp_auth")]
    [InlineData("GraphQL:warn_allow_all_cors_origins", "Gateway:Insecure:warn_allow_all_cors_origins")]
    [InlineData("WebSql:warn_allow_dml", "Gateway:WebSql:AllowDml")]
    [InlineData("WebSql:danger_bypass_sql_governance", "Gateway:Insecure:danger_bypass_websql_governance")]
    public void RemovedKeySetToTrue_FailsWithReplacement(string oldKey, string newKey)
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => LegacySwitchGuard.ThrowIfLegacyKeysSet(Config(("Gateway:" + oldKey, "true"))));
        ex.Message.ShouldContain("Gateway:" + oldKey);
        ex.Message.ShouldContain(newKey);
    }

    [Fact]
    public void RemovedKeySetToFalse_OrAbsent_IsTolerated()
    {
        LegacySwitchGuard.ThrowIfLegacyKeysSet(Config(("Gateway:Mcp:danger_bypass_mcp_auth", "false")));
        LegacySwitchGuard.ThrowIfLegacyKeysSet(Config());
    }

    [Fact]
    public void NewInsecureKey_IsNotFlagged() =>
        LegacySwitchGuard.FindViolations(Config(("Gateway:Insecure:danger_bypass_mcp_auth", "true"))).ShouldBeEmpty();

    [Fact]
    public void EveryReplacement_PointsToAnExistingOption()
    {
        foreach (var target in LegacySwitchGuard.Replacements.Values)
        {
            var parts = target.Split(':');
            var section = typeof(GatewayOptions).GetProperty(parts[0])!.PropertyType;
            section.GetProperty(parts[1], BindingFlags.Public | BindingFlags.Instance)
                .ShouldNotBeNull($"{target} must exist");
        }
    }
}
