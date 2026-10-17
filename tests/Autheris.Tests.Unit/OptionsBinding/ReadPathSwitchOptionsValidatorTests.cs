namespace Autheris.Tests.Unit.OptionsBinding;

using System;
using System.IO;
using System.Linq;
using System.Text;
using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;

/// <summary>
/// PLAN-SEC-115 A-8 and C-115-2 (SEC-115-02): a closing switch must never be silently ignored. Validates the raw configuration
/// (what the operator wrote), not only the bound options, and independent of <c>Mcp:Enabled</c>.
/// </summary>
public sealed class ReadPathSwitchOptionsValidatorTests
{
    private static IConfiguration Config(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    private static ValidateOptionsResult Run(string json)
    {
        var config = Config(json);
        GatewayOptions options;
        try
        {
            options = config.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
        }
        catch (InvalidOperationException)
        {
            // The binder itself throws for an unparsable boolean (the start aborts there); isolate the validator's own check.
            options = new GatewayOptions();
        }

        return new ReadPathSwitchOptionsValidator(config).Validate(null, options);
    }

    private static ValidateOptionsResult RunOptionsOnly(GatewayOptions options) =>
        new ReadPathSwitchOptionsValidator().Validate(null, options);

    [Fact]
    public void All_defaults_pass()
    {
        RunOptionsOnly(new GatewayOptions()).Succeeded.ShouldBeTrue();
        Run("{}").Succeeded.ShouldBeTrue();
        Run("""{"Gateway":{"Mcp":{"Enabled":true}}}""").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Valid_hardened_profile_passes()
    {
        Run("""
            {"Gateway":{"RestApi":{"DataEndpoint":{"Enabled":false}},
              "Mcp":{"Tools":{"Catalog":true,"Query":false,"Api":"False","Access":true},
                     "DisabledTools":["query_sql"," admin_apply_access ","QUERY_SQL"]}}}
            """).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Unknown_tool_name_fails_the_start_and_lists_valid_names()
    {
        var result = RunOptionsOnly(new GatewayOptions { Mcp = new McpOptions { DisabledTools = ["query_sqll"] } });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Gateway:Mcp:DisabledTools");
        result.FailureMessage.ShouldContain("query_sqll");
        result.FailureMessage.ShouldContain("query_sql");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_entry_fails(string entry)
    {
        RunOptionsOnly(new GatewayOptions { Mcp = new McpOptions { DisabledTools = [entry] } }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Valid_builtin_names_pass()
    {
        RunOptionsOnly(new GatewayOptions { Mcp = new McpOptions { DisabledTools = ["query_sql", "ADMIN_APPLY_ACCESS", "search_docs"] } })
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void AllowedOperations_derived_name_is_accepted()
    {
        RunOptionsOnly(new GatewayOptions { Mcp = new McpOptions { AllowedOperations = ["List Orders"], DisabledTools = ["list_orders"] } })
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Empty_list_and_null_pass()
    {
        RunOptionsOnly(new GatewayOptions { Mcp = new McpOptions { DisabledTools = [] } }).Succeeded.ShouldBeTrue();
        RunOptionsOnly(new GatewayOptions { Mcp = new McpOptions { DisabledTools = null } }).Succeeded.ShouldBeTrue();
        Run("""{"Gateway":{"Mcp":{"DisabledTools":[]}}}""").Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"Gateway":{"RestApi":{"DataEndpoint":{"Enable":false}}}}""", "Gateway:RestApi:DataEndpoint")]
    [InlineData("""{"Gateway":{"RestApi":{"DataEndpoints":{"Enabled":false}}}}""", "Gateway:RestApi")]
    [InlineData("""{"Gateway":{"Mcp":{"Tools":{"Qeury":false}}}}""", "Gateway:Mcp:Tools")]
    [InlineData("""{"Gateway":{"RestApi":{"DataEndpoint":false}}}""", "Gateway:RestApi:DataEndpoint")]
    [InlineData("""{"Gateway":{"RestApi":false}}""", "Gateway:RestApi")]
    [InlineData("""{"Gateway":{"Mcp":{"Tools":false}}}""", "Gateway:Mcp:Tools")]
    [InlineData("""{"Gateway":{"Mcp":{"DisabledTools":"query_sql"}}}""", "Gateway:Mcp:DisabledTools")]
    [InlineData("""{"Gateway":{"RestApi":{"DataEndpoint":{"Enabled":null}}}}""", "Gateway:RestApi:DataEndpoint:Enabled")]
    [InlineData("""{"Gateway":{"Mcp":{"Tools":{"Query":null}}}}""", "Gateway:Mcp:Tools:Query")]
    [InlineData("""{"Gateway":{"Mcp":{"Tools":{"Query":""}}}}""", "Gateway:Mcp:Tools:Query")]
    [InlineData("""{"Gateway":{"RestApi":{"DataEndpoint":{"Enabled":"yes"}}}}""", "Gateway:RestApi:DataEndpoint:Enabled")]
    [InlineData("""{"Gateway":{"RestApi":{"DataEndpoint":{"Enabled":"0"}}}}""", "Gateway:RestApi:DataEndpoint:Enabled")]
    [InlineData("""{"Gateway":{"Mcp":{"Tools":{"Api":"off"}}}}""", "Gateway:Mcp:Tools:Api")]
    [InlineData("""{"Gateway":{"Mcp":{"Tools":{"Access":1}}}}""", "Gateway:Mcp:Tools:Access")]
    [InlineData("""{"Gateway":{"Mcp":{"DisabledTools":[null]}}}""", "Gateway:Mcp:DisabledTools:0")]
    [InlineData("""{"Gateway":{"Mcp":{"DisabledTools":["query_sql",""]}}}""", "Gateway:Mcp:DisabledTools:1")]
    [InlineData("""{"Gateway":{"Mcp":{"DisabledTools":{"a":"query_sql"}}}}""", "Gateway:Mcp:DisabledTools")]
    [Trait("Category", "Security")]
    public void ST_115_03_Malformed_switch_configuration_fails_the_start_naming_the_key(string json, string key)
    {
        var result = Run(json);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(key);
    }

    [Theory]
    [InlineData("""{"Gateway":{"Mcp":{"Enabled":false,"Tools":{"Qeury":false}}}}""")]
    [InlineData("""{"Gateway":{"Mcp":{"Enabled":false,"Tools":{"Query":"yes"}}}}""")]
    [InlineData("""{"Gateway":{"Mcp":{"Enabled":false,"DisabledTools":"query_sql"}}}""")]
    [InlineData("""{"Gateway":{"Mcp":{"Enabled":false,"DisabledTools":["query_sqll"]}}}""")]
    [InlineData("""{"Gateway":{"Mcp":{"Enabled":false,"DisabledTools":[""]}}}""")]
    [InlineData("""{"Gateway":{"Mcp":{"Enabled":false},"RestApi":{"DataEndpoint":{"Enable":false}}}}""")]
    [Trait("Category", "Security")]
    public void ST_115_04_The_same_invalid_input_fails_with_Mcp_disabled(string json)
    {
        Run(json).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Failure_messages_never_echo_switch_values()
    {
        var result = Run("""{"Gateway":{"Mcp":{"Tools":{"Api":"s3cr3t-value"}}}}""");

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotContain("s3cr3t-value");
    }

    [Fact]
    public void Binding_reads_the_new_options_and_an_empty_list_binds_to_null()
    {
        var config = Config("""
            {"Gateway":{"RestApi":{"DataEndpoint":{"Enabled":false}},
              "Mcp":{"Tools":{"Query":false},"DisabledTools":["query_sql"]}}}
            """);
        var o = config.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>()!;

        o.RestApi.DataEndpoint.Enabled.ShouldBeFalse();
        o.Mcp.Tools.Query.ShouldBeFalse();
        o.Mcp.Tools.Catalog.ShouldBeTrue();
        o.Mcp.DisabledTools.ShouldBe(["query_sql"]);

        var empty = Config("""{"Gateway":{"Mcp":{"DisabledTools":[]}}}""").GetSection(GatewayOptions.SectionName).Get<GatewayOptions>();
        (empty?.Mcp.DisabledTools ?? []).Count.ShouldBe(0);
    }
}
