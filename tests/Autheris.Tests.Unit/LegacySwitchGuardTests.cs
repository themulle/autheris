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

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("SECRET-VALUE-MUST-NOT-LEAK")]
    public void Legacy_Switch_Guard_Fails_On_Presence_Even_When_False(string value)
    {
        // DEF-SC-11: a removed key fails startup on presence, whatever its value; the message names the key and never the value.
        var ex = Should.Throw<InvalidOperationException>(
            () => LegacySwitchGuard.ThrowIfLegacyKeysSet(Config(("Gateway:Mcp:danger_bypass_mcp_auth", value))));

        ex.Message.ShouldContain("Gateway:Mcp:danger_bypass_mcp_auth");
        ex.Message.ShouldNotContain(value);
        LegacySwitchGuard.ThrowIfLegacyKeysSet(Config());
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("Gateway:Tenancy:Mode", "Single", "Gateway:Tenancy:DefaultTenantId")]
    [InlineData("Gateway:Tenancy:Mode", "Multi", "Gateway:Tenancy:DefaultTenantId")]
    [InlineData("Gateway:Tenancy:Mode", "", "Gateway:Tenancy:DefaultTenantId")]
    [InlineData("Gateway:Authentication:ForwardAuth:DefaultTenantId", "tenant-a", "Gateway:Tenancy:DefaultTenantId")]
    [InlineData("Gateway:Authentication:ForwardAuth:DefaultTenantId", "", "Gateway:Tenancy:DefaultTenantId")]
    public void DT_SC_13_Removed_Tenancy_Keys_Fail_Startup_On_Presence_Whatever_The_Value(string key, string value, string replacement)
    {
        var ex = Should.Throw<InvalidOperationException>(() => LegacySwitchGuard.ThrowIfLegacyKeysSet(Config((key, value))));

        ex.Message.ShouldContain(key);
        ex.Message.ShouldContain(replacement);
        if (value.Length > 0)
        {
            ex.Message.ShouldNotContain(value);
        }
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("Gateway__Tenancy__Mode", "Single")]
    [InlineData("Gateway__Authentication__ForwardAuth__DefaultTenantId", "tenant-a")]
    public void DT_SC_13_Removed_Tenancy_Keys_Fail_Startup_In_Environment_Spelling(string variable, string value)
    {
        // The environment configuration provider maps '__' to ':', so the environment spelling is the same key.
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            Should.Throw<InvalidOperationException>(() => LegacySwitchGuard.ThrowIfLegacyKeysSet(configuration))
                .Message.ShouldContain(variable.Replace("__", ":", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("Quickstart")]
    [InlineData("Strict")]
    public void Gateway_Profile_Key_Fails_Startup(string value)
    {
        // L2: Gateway:Profile (alias of Dev:Preset) is removed.
        var ex = Should.Throw<InvalidOperationException>(() => LegacySwitchGuard.ThrowIfLegacyKeysSet(Config(("Gateway:Profile", value))));

        ex.Message.ShouldContain("Gateway:Profile");
        ex.Message.ShouldContain("Gateway:Dev:Preset");
        ex.Message.ShouldNotContain(value);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Legacy_WebSql_Dml_Alias_Fails_Startup()
    {
        // L3: Insecure.warn_allow_websql_dml (alias of WebSql.AllowDml) is removed.
        var ex = Should.Throw<InvalidOperationException>(
            () => LegacySwitchGuard.ThrowIfLegacyKeysSet(Config(("Gateway:Insecure:warn_allow_websql_dml", "false"))));

        ex.Message.ShouldContain("Gateway:Insecure:warn_allow_websql_dml");
        ex.Message.ShouldContain("Gateway:WebSql:AllowDml");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Insecure_Webhook_Tenant_Fallback_Key_Fails_Startup()
    {
        // L6: the unbound-webhook tenant fallback is removed (unbound webhooks are always rejected); the key fails startup with a removal message.
        var ex = Should.Throw<InvalidOperationException>(
            () => LegacySwitchGuard.ThrowIfLegacyKeysSet(Config(("Gateway:Insecure:warn_fallback_default_tenant_for_webhooks", "true"))));

        ex.Message.ShouldContain("Gateway:Insecure:warn_fallback_default_tenant_for_webhooks");
        ex.Message.ShouldContain("removed");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("")]
    public void DT_SC_13_FailClosedOnRevocationLookupError_Key_Fails_Startup_On_Presence(string value)
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => LegacySwitchGuard.ThrowIfLegacyKeysSet(Config(("Gateway:Caching:Redis:FailClosedOnRevocationLookupError", value))));

        ex.Message.ShouldContain("Gateway:Caching:Redis:FailClosedOnRevocationLookupError");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void DT_SC_13_FailClosedOnRevocationLookupError_Env_Spelling_Fails_Startup()
    {
        const string name = "Gateway__Caching__Redis__FailClosedOnRevocationLookupError";
        Environment.SetEnvironmentVariable(name, "false");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            LegacySwitchGuard.FindViolations(configuration)
                .ShouldContain(m => m.Contains("Caching:Redis:FailClosedOnRevocationLookupError", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
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
