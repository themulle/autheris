namespace Autheris.Tests.Unit.OptionsBinding;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

/// <summary>The binder appends to pre-filled lists; the Agent.Read default must not be merged with configuration.</summary>
public class EntraReadOnlyScopesBindingTests
{
    private static EntraIdAuthOptions Bind(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return config.Get<EntraIdAuthOptions>() ?? new EntraIdAuthOptions();
    }

    [Fact]
    public void SingleConfiguredScope_IsNotMergedWithDefault()
    {
        Bind(new() { ["ReadOnlyScopes:0"] = "api://autheris/Agent.Read" })
            .GetEffectiveReadOnlyScopes().ToArray().ShouldBe(["api://autheris/Agent.Read"]);
    }

    [Fact]
    public void NothingConfigured_UsesAgentReadAndAppReadDefault()
    {
        Bind(new() { ["Enabled"] = "true" })
            .GetEffectiveReadOnlyScopes().ToArray().ShouldBe(["Agent.Read", "App.Read"]);
    }

    [Fact]
    public void ProgrammaticEmptyList_HasNoReadOnlyScope()
    {
        new EntraIdAuthOptions { ReadOnlyScopes = [] }.GetEffectiveReadOnlyScopes().ShouldBeEmpty();
    }
}
