namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Common;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Configuration.StartupValidatorGoldenSupport;

/// <summary>
/// .NET 11 chained configuration: an empty string from a later provider overrides the value of an earlier provider instead of
/// being ignored. Required security settings must therefore treat an empty or whitespace override as missing and fail startup
/// (fail-closed); flags that weaken security must never be switched on by an empty override.
/// </summary>
[Collection(GoldenConsoleCollection.Name)]
[Trait("Category", "Security")]
public sealed class EmptyConfigurationValueFailClosedTests : IDisposable
{
    private readonly string _fixtureDir = CreateFixtureDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_fixtureDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup of the temp fixture
        }
    }

    [Fact]
    public void Baseline_ChainedProductionConfiguration_Validates()
    {
        var options = BindChained(overrides: []);

        Should.NotThrow(() => Validate(options, "Production"));
    }

    [Theory]
    [InlineData("DataMasking:HmacSecretKeyVaultRef", "")]
    [InlineData("DataMasking:HmacSecretKeyVaultRef", " ")]
    [InlineData("Audit:ChainAnchorPath", "")]
    [InlineData("Audit:ChainAnchorPath", " ")]
    public void RequiredSecuritySetting_EmptyOverrideFromLaterProvider_FailsStartup(string key, string value)
    {
        var options = BindChained(overrides: [(key, value)]);

        Should.Throw<ValidationException>(() => Validate(options, "Production"));
    }

    [Theory]
    [InlineData("Authentication:EnableTestAuthHandler")]
    [InlineData("GovernanceDb:SeedDemoData")]
    [InlineData("Mcp:EnableDeveloperCors")]
    [InlineData("GraphQL:EnableIntrospection")]
    [InlineData("Plugins:RequireIntegrityManifest")]
    public void BooleanSecurityFlag_EmptyOverrideFromLaterProvider_NeverYieldsAValidatedConfiguration(string key)
    {
        // An empty value for a boolean either fails binding (startup aborts) or leaves the secure Production baseline unchanged;
        // it must never silently produce a different, accepted security posture.
        GatewayOptions? options = null;
        var bindFailure = Record.Exception(() => options = BindChained(overrides: [(key, string.Empty)]));

        if (bindFailure is not null)
        {
            bindFailure.ShouldBeOfType<InvalidOperationException>();
            bindFailure.Message.ShouldContain(key, Case.Sensitive);
            return;
        }

        Should.NotThrow(() => Validate(options!, "Production"));
    }

    private GatewayOptions BindChained((string Key, string? Value)[] overrides)
    {
        var baseValues = ProductionBase.ToDictionary(
            kv => "Gateway:" + kv.Key,
            kv => kv.Value?.Replace("{tmp}", _fixtureDir, StringComparison.Ordinal));
        var laterValues = overrides.ToDictionary(o => "Gateway:" + o.Key, o => o.Value);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(baseValues)
            .AddInMemoryCollection(laterValues)
            .Build();

        return configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
    }

    private static void Validate(GatewayOptions options, string environment) =>
        GatewayStartupValidator.ValidateStartup(
            options,
            HostEnvironment(environment),
            _ => null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
}
