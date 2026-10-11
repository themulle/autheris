namespace Autheris.Tests.Unit.OptionsBinding;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

/// <summary>
/// The configuration binder appends to pre-filled lists, so a non-empty default could never be narrowed by an operator.
/// The default applies only when nothing is configured.
/// </summary>
public class ReverseProxyKnownNetworksBindingTests
{
    private static ReverseProxyOptions Bind(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return config.Get<ReverseProxyOptions>() ?? new ReverseProxyOptions();
    }

    [Fact]
    public void SingleConfiguredNetwork_IsNotMergedWithLoopbackDefault()
    {
        Bind(new() { ["KnownNetworks:0"] = "10.0.0.0/8" })
            .GetEffectiveKnownNetworks().ToArray().ShouldBe(["10.0.0.0/8"]);
    }

    [Fact]
    public void NothingConfigured_UsesLoopbackDefault()
    {
        Bind(new() { ["Enabled"] = "true" })
            .GetEffectiveKnownNetworks().ToArray().ShouldBe(["127.0.0.1/32", "::1/128"]);
    }

    [Fact]
    public void ProgrammaticEmptyList_TrustsNoNetwork()
    {
        new ReverseProxyOptions { KnownNetworks = [] }.GetEffectiveKnownNetworks().ShouldBeEmpty();
    }
}
