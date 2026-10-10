namespace Autheris.Tests.Unit.OptionsBinding;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

/// <summary>The binder appends to pre-filled lists; the break-glass role default must not be merged with configuration.</summary>
public class BreakGlassAllowedRolesBindingTests
{
    private static ExtensibilityOptions Bind(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return config.Get<ExtensibilityOptions>() ?? new ExtensibilityOptions();
    }

    [Fact]
    public void SingleConfiguredRole_IsNotMergedWithDefaults()
    {
        Bind(new() { ["BreakGlassAllowedRoles:0"] = "BreakGlassOperator" })
            .GetEffectiveBreakGlassAllowedRoles().ToArray().ShouldBe(["BreakGlassOperator"]);
    }

    [Fact]
    public void NothingConfigured_UsesDocumentedDefault()
    {
        Bind(new() { ["Enabled"] = "true" })
            .GetEffectiveBreakGlassAllowedRoles().ToArray()
            .ShouldBe(["BreakGlassOperator", "ClusterAdmin", "GovernanceAdmin", "SecurityAdmin"]);
    }

    [Fact]
    public void ProgrammaticEmptyList_AuthorizesNobody()
    {
        new ExtensibilityOptions { BreakGlassAllowedRoles = [] }.GetEffectiveBreakGlassAllowedRoles().ShouldBeEmpty();
    }
}
