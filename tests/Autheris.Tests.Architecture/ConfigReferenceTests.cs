using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SEC-SIMPL-61: Security guards for configuration reference generation:
/// secret masking, default value prohibitions, and process isolation (no config/env reads).
/// </summary>
[Collection("HostStartupSequential")]
[Trait("Category", "Security")]
public sealed class ConfigReferenceTests
{
    private sealed class FakeOptionsWithDefaultSecret
    {
        public string ClientSecret { get; set; } = "hardcoded-secret-password-123";
    }

    private sealed class FakeOptionsWithEmptySecret
    {
        public string SharedSecret { get; set; } = "";
    }

    [Fact]
    public void Secret_like_options_never_render_values_or_defaults()
    {
        // 1. A type with a non-empty defaulted secret must make the generator throw
        Should.Throw<InvalidOperationException>(() =>
        {
            ConfigurationReferenceGenerator.GenerateSection("Gateway:Test", typeof(FakeOptionsWithDefaultSecret));
        });

        // 2. A secret-like property with empty or null default renders <secret> in the Default column
        var md = ConfigurationReferenceGenerator.GenerateSection("Gateway:Test", typeof(FakeOptionsWithEmptySecret));
        md.ShouldContain("<secret>");
        md.ShouldNotContain("hardcoded-secret");

        // 3. Across the entire generated configuration reference, secret-like properties render <secret>
        var fullDoc = ConfigurationReferenceGenerator.GenerateMarkdown();
        fullDoc.ShouldContain("<secret>");
    }

    [Fact]
    public void Secret_properties_have_no_default()
    {
        // The generator throws while reflecting when a secret-like option has a non-empty or non-null default.
        var rows = ConfigurationReferenceGenerator.CollectAllRows();
        rows.Count.ShouldBeGreaterThan(0);

        var secretRows = rows
            .Where(r => ConfigurationReferenceGenerator.IsSecretLike(r.Key[(r.Key.LastIndexOf(':') + 1)..]) && r.Type != "bool")
            .ToList();
        secretRows.ShouldNotBeEmpty("No secret-like option rows found; the secret detection is broken.");
        secretRows.Where(r => r.Default != "<secret>").Select(r => r.Key).ShouldBeEmpty(
            "Secret-like options must render <secret> and carry no default value.");
    }

    [Fact]
    public void Generator_does_not_read_configuration_or_environment()
    {
        const string canaryVar = "Gateway__Webhooks__Url";
        const string canaryVal = "leak-canary-secret-value-12345";
        Environment.SetEnvironmentVariable(canaryVar, canaryVal);

        try
        {
            var md = ConfigurationReferenceGenerator.GenerateMarkdown();
            md.ShouldNotContain(canaryVal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(canaryVar, null);
        }
    }
}
