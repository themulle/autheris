namespace Autheris.Tests.Unit.OptionsBinding;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

/// <summary>The binder appends to pre-filled lists; the header-role allowlist default must not be merged with configuration.</summary>
public class ForwardAuthAllowedRolesBindingTests
{
    private static ForwardAuthOptions Bind(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return config.Get<ForwardAuthOptions>() ?? new ForwardAuthOptions();
    }

    [Fact]
    public void SingleConfiguredRole_IsNotMergedWithDefaults()
    {
        Bind(new() { ["AllowedRoles:0"] = "Analyst" })
            .GetEffectiveAllowedRoles().ToArray().ShouldBe(["Analyst"]);
    }

    [Fact]
    public void NothingConfigured_UsesDocumentedDefault()
    {
        Bind(new() { ["Enabled"] = "true" })
            .GetEffectiveAllowedRoles().ToArray().ShouldBe(["GatewayUser", "Consumer", "Analyst", "Developer"]);
    }

    [Fact]
    public void ProgrammaticEmptyList_AcceptsNoHeaderRole()
    {
        new ForwardAuthOptions { AllowedRoles = [] }.GetEffectiveAllowedRoles().ShouldBeEmpty();
    }
}
