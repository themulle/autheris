namespace Autheris.Tests.Unit.OptionsBinding;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

/// <summary>The binder appends to pre-filled lists; the Development default must not be merged with configuration.</summary>
public class BasicAuthSessionAllowedEnvironmentsBindingTests
{
    private static BasicAuthSessionOptions Bind(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return config.Get<BasicAuthSessionOptions>() ?? new BasicAuthSessionOptions();
    }

    [Fact]
    public void SingleConfiguredEnvironment_IsNotMergedWithDevelopmentDefault()
    {
        Bind(new() { ["AllowedEnvironments:0"] = "Integration" })
            .GetEffectiveAllowedEnvironments().ToArray().ShouldBe(["Integration"]);
    }

    [Fact]
    public void NothingConfigured_UsesDevelopmentDefault()
    {
        Bind(new() { ["Enabled"] = "true" })
            .GetEffectiveAllowedEnvironments().ToArray().ShouldBe(["Development"]);
    }

    [Fact]
    public void ProgrammaticEmptyList_AllowsNoEnvironment()
    {
        new BasicAuthSessionOptions { AllowedEnvironments = [] }.GetEffectiveAllowedEnvironments().ShouldBeEmpty();
    }
}
