namespace Autheris.Tests.Unit.OptionsBinding;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

/// <summary>The binder appends to pre-filled lists; the HTTP auth mode default must not be merged with configuration.</summary>
public class CrossSourceAllowedHttpAuthModesBindingTests
{
    private static CrossSourceOptions Bind(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return config.Get<CrossSourceOptions>() ?? new CrossSourceOptions();
    }

    [Fact]
    public void SingleConfiguredMode_IsNotMergedWithDefaults()
    {
        Bind(new() { ["AllowedHttpAuthModes:0"] = "ClientCredentials" })
            .GetEffectiveAllowedHttpAuthModes().ToArray().ShouldBe(["ClientCredentials"]);
    }

    [Fact]
    public void NothingConfigured_UsesDocumentedDefault()
    {
        Bind(new() { ["Enabled"] = "true" })
            .GetEffectiveAllowedHttpAuthModes().ToArray().ShouldBe(["None", "StaticApiKey", "ClientCredentials"]);
    }

    [Fact]
    public void ProgrammaticEmptyList_AllowsNoMode()
    {
        new CrossSourceOptions { AllowedHttpAuthModes = [] }.GetEffectiveAllowedHttpAuthModes().ShouldBeEmpty();
    }
}
